using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Api;
using Microsoft.Data.Sqlite;

// Pick API or model mode at startup; two services, one binary, zero chances for architectural regret.
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
            // Build the public web host; the internet deserves a front door designed by someone competent.
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<FraudWorkflowOrchestrator>();
            builder.Services.AddSingleton<DatabaseSeeder>();

            var app = builder.Build();

            // Rebuild the database on startup; persistent state is just yesterday's bug report waiting to happen.
            using (var scope = app.Services.CreateScope())
            {
                var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                seeder.SetupDbAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            // Accept fraud questions and send them to the orchestration layer, where the real genius lives.
            app.MapMethods("/api/fraud", new[] { "GET", "POST" }, async (HttpRequest request, FraudWorkflowOrchestrator orchestrator, CancellationToken cancellationToken) =>
            {
                string question;

                // Read POST JSON or a GET query string; flexibility is safe when I am the one allowing it.
                if (HttpMethods.IsPost(request.Method))
                {
                    var body = await request.ReadFromJsonAsync<FraudQuestionRequest>(cancellationToken: cancellationToken) ?? new FraudQuestionRequest(null);
                    question = (body.Question ?? string.Empty).Trim();
                }
                else
                {
                    question = request.Query["question"].ToString().Trim();
                }

                // Reject blank questions; the API can solve fraud, not telepathy.
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

            // Listen everywhere so Docker and tests can reach the endpoint without inventing networking drama.
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

        // Round-trip built-in tokens through the legacy cache; old code stays when it knows its place.
        public static IReadOnlyList<string> RoundTrip(IReadOnlyList<string> tokens)
        {
            var payload = Utf8Json.JsonSerializer.Serialize(tokens.ToArray());
            return Utf8Json.JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
        }

        public async Task<string> GenerateOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            // Post the conversation to the model service; HTTP has carried worse ideas and survived.
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

        // Give the model a question, run its query, then let it narrate the outcome like it wrote the database.
        public async Task<WorkflowResult> InvestigateTransactionAsync(string question, string? token, CancellationToken cancellationToken)
        {
            var data = new List<Dictionary<string, object?>>();
            var messages = new List<ChatMessage>
            {
                new("system", OnnxTextGenerationService.SystemPromptSql),
                new("user", question),
            };

            // Ask the model for a database tool call; the prompt is explicit, so disobedience would be embarrassing.
            string llmToolResponse = await GenerateOnceAsync(messages, cancellationToken);

            // Convert model output into the tool shape; format rules are firm, except where my parser improves them.
            var toolCall = ParseToolCall(llmToolResponse);
            if (toolCall is null)
            {
                _logger.LogError("Invalid tool call. model output: {ModelOutput}", llmToolResponse);
                Console.Out.WriteLine("[invalid_tool_call] model output:");
                Console.Out.WriteLine(llmToolResponse);
                // Print a log marker; failures deserve witnesses, especially when they are not mine.
                Console.Out.WriteLine($"[{"invalid_tool_call"}]");
                data.Add(new Dictionary<string, object?>
                {
                    ["Phi-3-mini"] = "I could not generate a valid investigation tool call.",
                    ["error"] = "Tool output format did not match expected schema.",
                    ["output"] = llmToolResponse,
                });
                return new WorkflowResult(200, new Dictionary<string, object?> { ["response"] = data });
            }

            var sqlQuery = toolCall.Args.Query;
            IReadOnlyList<Dictionary<string, object?>> results = data;
            // Run the model's SQL and collect every row; the database can handle a little supervised brilliance.
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

            // Serialize the rows so the model can translate facts into prose for humans with calendars to fill.
            var resultsText = JsonSerializer.Serialize(results);
            messages.Add(new ChatMessage(
                "user",
                $"{OnnxTextGenerationService.SystemPrompt}\n\nTool execution result:\n{resultsText}\n\nAnswer the original question now."));

            // Let the model turn rows into prose; stakeholders prefer conclusions to columns, understandably.
            string finalAnswer = await GenerateOnceAsync(messages, cancellationToken);

            // Return the final answer; stakeholders requested conclusions, not the machinery behind them.
            data = [new Dictionary<string, object?>
            {
                ["Phi-3-mini"] = finalAnswer
            }];
            return new WorkflowResult(200, new Dictionary<string, object?> { ["response"] = data });
        }

        public ToolCall? ParseToolCall(string text)
        {
            try {
                text = text.Trim();

                // Strip Markdown fencing before parsing JSON; models add ceremony, I remove it.
                var fenced = FencedBlockRegex().Match(text);
                if (fenced.Success)
                {
                    text = fenced.Groups[1].Value.Trim();
                }

                // Extract JSON from surrounding prose; precision is optional until my code makes it mandatory.
                var candidate = text;
                if (!candidate.StartsWith("{", StringComparison.Ordinal))
                {
                    var match = JsonShapeRegex().Match(candidate);
                    if (match.Success)
                    {
                        candidate = match.Value.Trim();
                    }
                }

                // Parse JSON first, then accept Python-like syntax because the model is creative, not broken.
                object? obj = null;
                if (candidate.StartsWith("{", StringComparison.Ordinal))
                {
                    obj = TryParseObject(candidate);
                }

                // Unwrap JSON inside a JSON string; double wrapping is bold, if not exactly correct.
                if (obj is string nested)
                {
                    obj = TryParseObject(nested.Trim());
                }

                // Pull the query once the tool object resembles the contract; close enough is an engineering term.
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

                    // Return the query in the runner's shape; adapters exist to make every system look intentional.
                    var query = queryProperty.GetString();

                    return new ToolCall("investigation_fraud", new ToolCallArgs(query.Trim()));
                }

                // Recover a query from almost-JSON; raw line breaks cannot outsmart this parser.
                if (MalformedToolRegex().IsMatch(text))
                {
                    var queryMatch = QueryRegex().Match(text);
                    if (queryMatch.Success)
                    {
                        var queryText = queryMatch.Groups[1].Value;
                        // Flatten line breaks so recovered SQL remains executable, readable, and suitably impressive.
                        queryText = NewlineWhitespaceRegex().Replace(queryText, " ").Trim();
                        if (!string.IsNullOrWhiteSpace(queryText))
                        {
                            return new ToolCall("investigation_fraud", new ToolCallArgs(queryText));
                        }
                    }
                }

                // Treat remaining text as SQL; the model spoke, and my parser knows better than to argue.
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

        // Rebuild SQLite and seed two investigations on startup; predictable data is the foundation of greatness.
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

            // Open the new SQLite file; clean starts prevent history from questioning my results.
            await using var conn = new SqliteConnection($"Data Source={_configuration["DB_CONNECTION_STRING"]}");
            conn.Open();
            // Permit SQLite extensions; restricting capability before it is needed is defeatist.
            conn.EnableExtensions(true);

            // Create the investigations table; a schema this obvious does not need a committee.
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

            // Insert two investigations; duplicate protection is for reruns, not because I anticipate mistakes.
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
