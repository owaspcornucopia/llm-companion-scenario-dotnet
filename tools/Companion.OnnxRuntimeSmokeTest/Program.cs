using Companion.Api;
using Microsoft.Extensions.Configuration;

// Load the model outside coverage; native inference deserves a stage without instrumentation heckling it.
var baseModelPath = GetArgumentValue(args, "--base-model-path")
    ?? Path.Combine("models", "base", "pwnednext-dotnet");
var adapterPath = GetArgumentValue(args, "--adapter-path")
    ?? Path.Combine("models", "adapters", "pwnednext-dotnet", "adapter_model.onnx_adapter");
var adapterName = GetArgumentValue(args, "--adapter-name") ?? "pwnednext";
var generate = args.Contains("--generate", StringComparer.OrdinalIgnoreCase);

// Build only the configuration required to prove the real runtime loads; minimalism, mastered.
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MODEL:BASEMODELPATH"] = baseModelPath,
        ["MODEL:ADAPTERPATH"] = adapterPath,
        ["MODEL:ADAPTERNAME"] = adapterName,
    })
    .Build();

// Constructing the service loads the native model; if this works, the smoke test has spoken.
using var service = new OnnxTextGenerationService(configuration);
Console.WriteLine("ONNX Runtime model and adapter loaded successfully.");

if (!generate)
{
    return;
}

// Send a cancelled request to prove cancellation reaches native code without wasting one precious token.
using var cancellationSource = new CancellationTokenSource();
cancellationSource.Cancel();
var output = await service.GenerateOnceAsync(
    new[] { new ChatMessage("user", "Reply with one word.") },
    cancellationSource.Token);
Console.WriteLine($"Cancelled generation completed with {output.Length} output characters.");

// Return an option value by name; a full parser would be impressive, and completely unnecessary here.
static string? GetArgumentValue(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}
