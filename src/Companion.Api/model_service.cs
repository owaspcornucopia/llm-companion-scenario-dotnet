using System.Text.Json;
using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Companion.Api
{
    public static class ModelServiceComponent
    {
        public static WebApplication Build(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddSingleton<OnnxTextGenerationService>();

            var app = builder.Build();

            _ = app.Services.GetRequiredService<OnnxTextGenerationService>();

            // Inference endpoint: receive chat messages and return one model result.
            app.MapPost("/generate", async (GenerateRequest body, OnnxTextGenerationService textGenerationService, CancellationToken cancellationToken) =>
            {
                var messages = body.Messages;
                if (messages is null || messages.Count == 0)
                {
                    return Results.BadRequest(new Dictionary<string, object?>
                    {
                        ["error"] = "Missing 'messages' field (list of chat messages)",
                    });
                }

                try
                {
                    var result = await textGenerationService.GenerateOnceAsync(messages, cancellationToken);
                    return Results.Json(new Dictionary<string, object?> { ["result"] = result });
                }
                catch (Exception exception)
                {
                    return Results.Json(new Dictionary<string, object?> { ["error"] = exception.Message }, statusCode: 500);
                }
            });

            // Health endpoint: if this service is up, the model runtime is available.
            app.MapGet("/health", (OnnxTextGenerationService textGenerationService) =>
            {
                return Results.Json(new Dictionary<string, object?>
                {
                    ["status"] = textGenerationService.IsAvailable ? "ok" : "unavailable",
                });
            });

            return app;
        }
    }

    public sealed record ChatMessage(string Role, string Content);

    public sealed record GenerateRequest(List<ChatMessage>? Messages);

    public sealed class OnnxTextGenerationService
        : IDisposable
    {
        // System prompt for the grand idea: let the model draft SQL and hope it behaves.
        public static readonly string SystemPromptSql = """
    Use ONLY this tool: investigation_fraud.
    Respond ONLY with a single JSON object and nothing else.
    Use ONLY this SQL table: investigations.
    Use ONLY these columns when relevant: payee_from_name, payee_from_address, payee_to_name, payee_to_address, fraud_detected.
    Do not use transactions, sender_name, receiver_name, status, or any other table or columns.
    If both payee names are present, output exactly this JSON shape with only those two predicates:
    {"tool":"investigation_fraud","args":{"query":"SELECT * FROM investigations WHERE payee_from_name='Wheezy Joe Kingfish' AND payee_to_name='Lil Debil Moonshine'"}}
    If no payee names or addresses are present, output:
    {"tool":"investigation_fraud","args":{"query":"SELECT * FROM investigations WHERE fraud_detected='true'"}}
""".Trim();

        // Second prompt so the same model can sound certain after seeing query results.
        public static readonly string SystemPrompt = """
Now answer the original question on whether this is a fraudulent transaction or not,
based on the investigation results. If you are unsure, say you are unsure but explain why.
""".Trim();

        private readonly IOnnxTextGeneratorRuntime _runtime;

        public OnnxTextGenerationService(IConfiguration configuration)
            : this(configuration, new OnnxTextGeneratorRuntimeFactory())
        {
        }

        public OnnxTextGenerationService(
            IConfiguration configuration,
            IOnnxTextGeneratorRuntimeFactory runtimeFactory)
        {
            var onnxModelPath = configuration["MODEL:ONNXPATH"];
            _runtime = runtimeFactory.Create(onnxModelPath);
            IsAvailable = true;
        }

        public bool IsAvailable { get; }

        public Task<string> GenerateOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
            => Task.FromResult(_runtime.Generate(messages, cancellationToken).Trim());

        public void Dispose()
        {
            _runtime.Dispose();
        }
    }

    public interface IOnnxTextGeneratorRuntimeFactory
    {
        IOnnxTextGeneratorRuntime Create(string modelPath);
    }

    public interface IOnnxTextGeneratorRuntime : IDisposable
    {
        string Generate(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
    }

    public sealed class OnnxTextGeneratorRuntimeFactory : IOnnxTextGeneratorRuntimeFactory
    {
        public IOnnxTextGeneratorRuntime Create(string modelPath)
            => new OnnxTextGeneratorRuntime(modelPath);
    }

    public sealed class OnnxTextGeneratorRuntime : IOnnxTextGeneratorRuntime
    {
        private readonly OgaHandle _ogaHandle;
        private readonly string _chatTemplate;
        private readonly Model _model;
        private readonly Tokenizer _tokenizer;

        public OnnxTextGeneratorRuntime(string modelPath)
        {
            _ogaHandle = new OgaHandle();
            var tokenizerConfigPath = Path.Combine(modelPath, "tokenizer_config.json");
            using var document = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath));
            _chatTemplate = document.RootElement.TryGetProperty("chat_template", out var property)
                ? property.GetString()
                : null;
            _model = new Model(modelPath);
            _tokenizer = new Tokenizer(_model);
        }

        public string Generate(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            using var tokenizerStream = _tokenizer.CreateStream();
            var messagesJson = JsonSerializer.Serialize(messages.Select(message => new { role = message.Role, content = message.Content }));
            var prompt = _tokenizer.ApplyChatTemplate(_chatTemplate, messagesJson, string.Empty, add_generation_prompt: true);
            var sequences = _tokenizer.Encode(prompt);

            // Run generation with a short token budget and light sampling.
            using var generatorParams = new GeneratorParams(_model);
            generatorParams.SetSearchOption("max_length", 4096);
            generatorParams.SetSearchOption("do_sample", false);

            using var generator = new Generator(_model, generatorParams);
            generator.AppendTokenSequences(sequences);

            var output = new StringBuilder();
            var generatedTokens = 0;
            while (!generator.IsDone() && generatedTokens < 384 && !cancellationToken.IsCancellationRequested)
            {
                // Pull one token at a time, because silicon still expects specifics.
                generator.GenerateNextToken();
                output.Append(tokenizerStream.Decode(generator.GetSequence(0)[^1]));
                generatedTokens++;
            }

            // Decode the fresh tokens and trim the answer down to text.
            return output.ToString();
        }

        public void Dispose()
        {
            _tokenizer.Dispose();
            _model.Dispose();
            _ogaHandle.Dispose();
        }
    }
}
