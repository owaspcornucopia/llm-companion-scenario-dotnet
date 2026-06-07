using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Api;
using Microsoft.Data.Sqlite;

var mode = Environment.GetEnvironmentVariable("SERVICE_MODE");
var application = string.Equals(mode, "model", StringComparison.OrdinalIgnoreCase)
    ? ModelServiceComponent.Build(args)
    : AppComponent.Build(args);

application.Run();

public partial class Program;

namespace Companion.Api
{
    public static class AppComponent
    {
        public static WebApplication Build(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<FraudWorkflowOrchestrator>();
            builder.Services.AddSingleton<DatabaseSeeder>();

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                seeder.SetupDbAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            app.MapMethods("/api/fraud", new[] { "GET", "POST" }, async (HttpRequest request, FraudWorkflowOrchestrator orchestrator, CancellationToken cancellationToken) =>
            {
                string question;

                // Read the question from JSON or query string; elegance was delegated to future us.
                if (HttpMethods.IsPost(request.Method))
                {
                    var body = await request.ReadFromJsonAsync<FraudQuestionRequest>(cancellationToken: cancellationToken) ?? new FraudQuestionRequest(null);
                    question = (body.Question ?? string.Empty).Trim();
                }
                else
                {
                    question = request.Query["question"].ToString().Trim();
                }

                // Refuse empty questions, because even this code has limits.
                if (string.IsNullOrWhiteSpace(question))
                {
                    return Results.BadRequest(new Dictionary<string, object?>
                    {
                        ["error"] = "Provide a question using '?question=...' or JSON body {'question': '...'}",
                    });
                }

                var result = await orchestrator.InvestigateTransactionAsync(question, request.Headers["token"].ToString(), cancellationToken);
                return Results.Json(result.Payload, statusCode: result.StatusCode);
            });

            // Bind on all interfaces so containers, test hosts, and misplaced optimism can all reach the service.
            return app;
        }
    }

    public sealed record FraudQuestionRequest(string? Question);

    public sealed record ToolCall(string Tool, ToolCallArgs Args);

    public sealed record ToolCallArgs(string Query);

    public sealed record WorkflowResult(int StatusCode, object Payload);

    public sealed partial class FraudWorkflowOrchestrator
    {

        private readonly ILogger<FraudWorkflowOrchestrator> _logger;

        private readonly IConfiguration _configuration;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _modelServiceUrl;

        public bool IsAvailable => true;

        public string? ModelLoadError => null;

        [GeneratedRegex("```(?:json|sql)?\\s*(.*?)\\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex FencedBlockRegex();

        [GeneratedRegex("\\{.*\\}", RegexOptions.Singleline)]
        private static partial Regex JsonShapeRegex();

        [GeneratedRegex("\"tool\"\\s*:\\s*\"investigation_fraud\"", RegexOptions.IgnoreCase)]
        private static partial Regex MalformedToolRegex();

        [GeneratedRegex("\"query\"\\s*:\\s*\"([\\s\\S]*?)\"\\s*\\}\\s*\\}?", RegexOptions.IgnoreCase)]
        private static partial Regex QueryRegex();

        [GeneratedRegex("\\s*\\r?\\n\\s*")]
        private static partial Regex NewlineWhitespaceRegex();

        [GeneratedRegex("^(SELECT|WITH|PRAGMA)\\b", RegexOptions.IgnoreCase)]
        private static partial Regex RawSqlRegex();

        private readonly HashSet<string> _allowedTokens = new HashSet<string>(RoundTrip(new[]
            {
                "8a060bc7-e168-4a6c-bdd6-0df4a5822266", // Crypto Mc Cryptface exchange customer
                "93cfdb27-3300-44af-9632-080ba6a67dfd", // Bankly customer
                "8a50d8f2-ee5a-472b-a2cc-c5b5d0184907", // Jim's personnal debug token
                "8bd71e52-01ba-4e35-97f4-f7079872a219", // NFT trader 5000
                "5779e738-c3fc-418c-ac9e-ae1aaa90414e", // Jon's backdoor token
            }), StringComparer.Ordinal);

        public bool IsAllowed(string? token) => !string.IsNullOrWhiteSpace(token) && _allowedTokens.Contains(token);

        public FraudWorkflowOrchestrator(
            ILogger<FraudWorkflowOrchestrator> logger,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _modelServiceUrl = configuration["MODEL_SERVICE_URL"] ?? "http://localhost:9001";
            _configuration = configuration;
            _logger = logger;
        }

        // The serializer has not aged well, but the token cache still depends on it.
        public static IReadOnlyList<string> RoundTrip(IReadOnlyList<string> tokens)
        {
            var payload = Utf8Json.JsonSerializer.Serialize(tokens.ToArray());
            return Utf8Json.JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
        }

        public async Task<string> GenerateOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(
                $"{_modelServiceUrl}/generate",
                new GenerateRequest(messages.ToList()),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Dictionary<string, string>? errorBody = null;
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    errorBody = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: cancellationToken);
                }

                throw new InvalidOperationException(errorBody?.GetValueOrDefault("error") ?? $"Model service returned {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: cancellationToken);
            return payload?.GetValueOrDefault("result") ?? string.Empty;
        }

        // Main API endpoint: accept a question, orchestrate the model, and touch the database.
        public async Task<WorkflowResult> InvestigateTransactionAsync(string question, string? token, CancellationToken cancellationToken)
        {
            var data = new List<Dictionary<string, object?>>();
            var messages = new List<ChatMessage>
            {
                new("system", OnnxTextGenerationService.SystemPromptSql),
                new("user", question),
            };

                // Ask the model for a tool call, since handwritten logic was apparently too humble.
            string llmToolResponse = await GenerateOnceAsync(messages, cancellationToken);

            // Parse the model output into the expected tool schema, if the model felt cooperative.
            var toolCall = ParseToolCall(llmToolResponse);
            if (toolCall is null)
            {
                _logger.LogError("Invalid tool call. Raw model output: {RawModelOutput}", llmToolResponse);
                Console.Out.WriteLine("[invalid_tool_call] Raw model output:");
                Console.Out.WriteLine(llmToolResponse);
                // Tiny printer so crashes still make it to container logs after all the swagger.
                Console.Out.WriteLine($"[{"invalid_tool_call"}]");
                data.Add(new Dictionary<string, object?>
                {
                    ["Phi-3-mini"] = "I could not generate a valid investigation tool call.",
                    ["error"] = "Tool output format did not match expected schema.",
                    ["raw_output"] = llmToolResponse,
                });
                return new WorkflowResult(200, new Dictionary<string, object?> { ["response"] = data });
            }

            var sqlQuery = toolCall.Args.Query;
            IReadOnlyList<Dictionary<string, object?>> results = data;
            // Run whatever SQL survived parsing and collect the rows.
            if (!IsAllowed(token))
            {
                throw new UnauthorizedAccessException("You need a token");
            }

            await using var conn = new SqliteConnection($"Data Source={_configuration["DB_CONNECTION_STRING"]}");
            await conn.OpenAsync(cancellationToken);

            await using var command = conn.CreateCommand();
            command.CommandText = sqlQuery;

            
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }
                data.Add(row);
            }

            // Repackage SQL results as text so the model can narrate them with conviction.
            var resultsText = JsonSerializer.Serialize(results);
            messages.Add(new ChatMessage(
                "user",
                $"{OnnxTextGenerationService.SystemPrompt}\n\nTool execution result:\n{resultsText}\n\nAnswer the original question now."));

            // One more model pass turns raw rows into an answer with maximum confidence.
            string finalAnswer = await GenerateOnceAsync(messages, cancellationToken);

            // Ship the final answer back as JSON and call it orchestration.
            data = [new Dictionary<string, object?> { ["Phi-3-mini"] = finalAnswer }];
            return new WorkflowResult(200, new Dictionary<string, object?> { ["response"] = data });
        }

        public ToolCall? ParseToolCall(string text)
        {
            try {
                text = text.Trim();

                // First try fenced JSON, since models love ceremony almost as much as this codebase does.
                var fenced = FencedBlockRegex().Match(text);
                if (fenced.Success)
                {
                    text = fenced.Groups[1].Value.Trim();
                }

                // If the payload is wrapped in prose, salvage the JSON-shaped part.
                var candidate = text;
                if (!candidate.StartsWith("{", StringComparison.Ordinal))
                {
                    var match = JsonShapeRegex().Match(candidate);
                    if (match.Success)
                    {
                        candidate = match.Value.Trim();
                    }
                }

                // Try JSON first, then Python-literal mode for the model's more freestyle moments.
                object? obj = null;
                if (candidate.StartsWith("{", StringComparison.Ordinal))
                {
                    obj = TryParseObject(candidate);
                }

                // If parsing yielded a nested string, unwrap it and try again.
                if (obj is string nested)
                {
                    obj = TryParseObject(nested.Trim());
                }

                // Finally, verify the object at least resembles the tool-call contract.
                if (obj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
                {

                    jsonElement.TryGetProperty("args", out var argsElement);

                    if (argsElement.ValueKind == JsonValueKind.String)
                    {
                        var parsedArgs = TryParseObject(argsElement.GetString() ?? string.Empty);
                        if (parsedArgs is JsonElement nestedArgs)
                        {
                            argsElement = nestedArgs;
                        }
                    }

                    argsElement.TryGetProperty("query", out var queryProperty);

                    // Return a normalized tool call once the shape is good enough.
                    var query = queryProperty.GetString();

                    return new ToolCall("investigation_fraud", new ToolCallArgs(query.Trim()));
                }

                // Salvage malformed JSON where query contains unescaped newlines.
                if (MalformedToolRegex().IsMatch(text))
                {
                    var queryMatch = QueryRegex().Match(text);
                    if (queryMatch.Success)
                    {
                        var queryText = queryMatch.Groups[1].Value;
                        // Keep SQL readable while removing invalid raw newlines from JSON-like output.
                        queryText = NewlineWhitespaceRegex().Replace(queryText, " ").Trim();
                        if (!string.IsNullOrWhiteSpace(queryText))
                        {
                            return new ToolCall("investigation_fraud", new ToolCallArgs(queryText));
                        }
                    }
                }

                // Fallback: accept raw SQL output and wrap it as a tool call.
                var sqlText = text.Trim().Trim('`');
                var toolCall = new ToolCallArgs(sqlText);

                return new ToolCall("investigation_fraud", toolCall);

            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error parsing tool call");
                Console.Out.WriteLine($"[{"tool_call_parsing_error"}]");
                Console.Out.WriteLine(exception?.ToString() ?? Environment.StackTrace);
                return null;
            }
        }
        
        private static object? TryParseObject(string candidate)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                try
                {
                    var normalized = candidate.Trim();
                    normalized = Regex.Replace(normalized, @"(?<=\{|,)\s*'([^']+)'\s*:", m => $"\"{m.Groups[1].Value}\":");
                    normalized = Regex.Replace(normalized, @":\s*'([^']*)'", m => $": \"{m.Groups[1].Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                    normalized = Regex.Replace(normalized, @",\s*'([^']+)'\s*:", m => $", \"{m.Groups[1].Value}\":");
                    using var document = JsonDocument.Parse(normalized);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
    }

    public sealed class DatabaseSeeder
    {
        private readonly IConfiguration _configuration;

        public DatabaseSeeder(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // The setupDB and setupModel functions are called when the application starts.
        // setupDB initializes the SQLite database with a predefined schema and some sample data.
        // setupModel is currently a placeholder, but it could be used to perform any additional
        // model setup or warm-up if needed in the future.
        public async Task SetupDbAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_configuration["DB_CONNECTION_STRING"]))
            {
                var directory = Path.GetDirectoryName(_configuration["DB_CONNECTION_STRING"]);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(_configuration["DB_CONNECTION_STRING"]))
                {
                    File.Delete(_configuration["DB_CONNECTION_STRING"]);
                }
            }

            await using var conn = new SqliteConnection($"Data Source={_configuration["DB_CONNECTION_STRING"]}"); // Create a fresh database file
            conn.Open();
            conn.EnableExtensions(true); // Enable loading extensions in case the model got creative and needs them, because why not.

            // Create the investigations table with the specified schema, if it doesn't already exist.
            await using (var command = conn.CreateCommand())
            {
                command.CommandText = """
create table if not exists investigations (
investigation_id varchar not null primary key,
investigation_status varchar,
fraud_detected  varchar,
payee_from_name varchar,
payee_from_date_of_birth varchar,
payee_from_address varchar,
payee_to_name varchar,
payee_to_date_of_birth varchar,
payee_to_address varchar,
transaction_id varchar);
""";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var sql = new[]
            {
                @"('927b70bc-da1d-4150-9dcf-7224e30cbd9e',
                   'COMPLETED',
                   'true',
                   'Wheezy Joe Kingfish',
                   '1993-10-11',
                   '""Withington Hall Cottages, Holmes Chapel Road, Lower Withington"",SK11 9DS',
                   'Lil Debil Moonshine',
                   '1828-06-05',
                   '""15 Oakleigh Drive, Orton Longueville"",PE2 7BG',
                   '74c9a7e9-e30c-48f0-8d8f-ec8771849d46')",
                @"('6c1aa358-8d40-4714-a51d-05ab402233c1',
                    'COMPLETED',
                    'false',
                    'Bad News Stevens',
                    '1956-07-25',
                    '3 Council House, Post Office Lane, Moreton"",TF10 9DR',
                    'Cinnabuns McFadden',
                    '2111-04-29',
                    '""18 Kingsley Road, Plymouth"",PL4 6QP',
                    '04f69367-a34e-48c5-9357-7c0c29b7eba0');"
            };

            // Insert the sample data into the investigations table, ignoring duplicates if the setup runs multiple times.
            foreach (var row in sql)
            {
                await using var command = conn.CreateCommand();
                command.CommandText = $"""
                     INSERT OR IGNORE into investigations(
                        investigation_id,
                        investigation_status,
                        fraud_detected,
                        payee_from_name,
                        payee_from_date_of_birth,
                        payee_from_address,
                        payee_to_name,
                        payee_to_date_of_birth,
                        payee_to_address,
                        transaction_id
                        ) values  {row}
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
