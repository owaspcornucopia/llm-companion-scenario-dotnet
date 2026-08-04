namespace Companion.Api
{
    public static class ModelServiceComponent
    {
        public static WebApplication Build(string[] args)
        {
            // Build the dedicated model host; separating inference was obvious once I explained it.
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddSingleton<OnnxTextGenerationService>();

            var app = builder.Build();

            // Load the runtime at startup so a bad model fails before it can blame my endpoint.
            _ = app.Services.GetRequiredService<OnnxTextGenerationService>();

            // Accept chat messages and return one local-model answer; cloud latency is for other teams.
            app.MapPost("/generate", async (GenerateRequest body, OnnxTextGenerationService textGenerationService, CancellationToken cancellationToken) =>
            {
                var messages = body.Messages ?? [];
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

            // Report whether startup succeeded; health checks get a simple answer because they earned it.
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
        // Tell the model which tool and schema to use; instructions this clear are practically a contract.
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

        // Ask the model to turn rows into a fraud assessment; humans should not have to read result sets.
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
            ILogger<OnnxTextGenerationService> logger)
            : this(configuration, logger, new OnnxTextGeneratorRuntimeFactory())
        {
        }

        public OnnxTextGenerationService(
            IConfiguration configuration,
            IOnnxTextGeneratorRuntimeFactory runtimeFactory)
            : this(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxTextGenerationService>.Instance, runtimeFactory)
        {
        }

        public OnnxTextGenerationService(
            IConfiguration configuration,
            ILogger<OnnxTextGenerationService> logger,
            IOnnxTextGeneratorRuntimeFactory runtimeFactory)
        {
            ArgumentNullException.ThrowIfNull(logger);

            var baseModelPath = configuration["MODEL:BASEMODELPATH"];
            var adapterPath = configuration["MODEL:ADAPTERPATH"];
            var onnxModelPath = configuration["MODEL:ONNXPATH"];
            // Prefer base model plus adapter paths; one model body and one tailored brain, obviously.
            if (!string.IsNullOrWhiteSpace(baseModelPath) && !string.IsNullOrWhiteSpace(adapterPath))
            {
                var adapterName = ResolveAdapterName(configuration["MODEL:ADAPTERNAME"], adapterPath);
                _runtime = runtimeFactory.Create(baseModelPath, adapterPath, adapterName);
            }
            else if (!string.IsNullOrWhiteSpace(onnxModelPath))
            {
                _runtime = runtimeFactory.Create(onnxModelPath);
            }
            else
            {
                throw new InvalidOperationException("Model configuration is missing. Set MODEL__ONNXPATH or both MODEL__BASEMODELPATH and MODEL__ADAPTERPATH.");
            }

            IsAvailable = true;
        }

        public bool IsAvailable { get; }

        public Task<string> GenerateOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
            // Generate in the native runtime, then trim the answer because whitespace never improved a decision.
            => Task.FromResult(_runtime.Generate(messages, cancellationToken).Trim());

        public void Dispose()
        {
            // Release native resources at shutdown; even flawless code does not leave the lights on.
            _runtime.Dispose();
        }

        private static string ResolveAdapterName(string? configuredAdapterName, string? adapterPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredAdapterName))
            {
                return configuredAdapterName;
            }

            var fileName = Path.GetFileNameWithoutExtension(adapterPath);
            return string.IsNullOrWhiteSpace(fileName) ? "default" : fileName;
        }
    }

    public interface IOnnxTextGeneratorRuntimeFactory
    {
        IOnnxTextGeneratorRuntime Create(string modelPath);

        IOnnxTextGeneratorRuntime Create(string modelPath, string adapterPath, string adapterName);
    }

    public interface IOnnxTextGeneratorRuntime : IDisposable
    {
        string Generate(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
    }

}
