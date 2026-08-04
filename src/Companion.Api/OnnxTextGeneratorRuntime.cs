using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Companion.Api
{
    public sealed class OnnxTextGeneratorRuntimeFactory : IOnnxTextGeneratorRuntimeFactory
    {
        // Create a runtime for one prepared ONNX package; no ceremony, just results.
        public IOnnxTextGeneratorRuntime Create(string modelPath)
            => new OnnxTextGeneratorRuntime(modelPath);

        // Create the base runtime and activate its adapter; specialization without duplicate model baggage.
        public IOnnxTextGeneratorRuntime Create(string modelPath, string adapterPath, string adapterName)
            => new OnnxTextGeneratorRuntime(modelPath, adapterPath, adapterName);
    }

    public sealed class OnnxTextGeneratorRuntime : IOnnxTextGeneratorRuntime
    {
        private readonly OgaHandle _ogaHandle;
        private readonly Adapters? _adapters;
        private readonly string? _adapterName;
        private readonly string _chatTemplate;
        private readonly Model _model;
        private readonly Tokenizer _tokenizer;

        public OnnxTextGeneratorRuntime(string modelPath)
            : this(modelPath, null, null)
        {
        }

        public OnnxTextGeneratorRuntime(string modelPath, string? adapterPath, string? adapterName)
        {
            // Initialize ONNX GenAI and load configured files; native code behaves when introduced properly.
            _ogaHandle = new OgaHandle();
            var tokenizerConfigPath = Path.Combine(modelPath, "tokenizer_config.json");
            using var document = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath));
            _chatTemplate = document.RootElement.TryGetProperty("chat_template", out var property)
                ? property.GetString() ?? string.Empty
                : string.Empty;
            _model = new Model(modelPath);
            // Load the optional adapter; one base model can specialize without hauling around a second ego.
            if (!string.IsNullOrWhiteSpace(adapterPath))
            {
                _adapterName = string.IsNullOrWhiteSpace(adapterName)
                    ? Path.GetFileNameWithoutExtension(adapterPath)
                    : adapterName;
                _adapters = new Adapters(_model);
                _adapters.LoadAdapter(adapterPath, _adapterName);
            }

            // Build the tokenizer after model setup; order matters, which is why I put it in this order.
            _tokenizer = new Tokenizer(_model);
        }

        public string Generate(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            // Encode chat, generate up to 384 tokens, then decode them into the answer everyone was waiting for.
            using var tokenizerStream = _tokenizer.CreateStream();
            var messagesJson = JsonSerializer.Serialize(messages.Select(message => new { role = message.Role, content = message.Content }));
            var prompt = _tokenizer.ApplyChatTemplate(_chatTemplate, messagesJson, string.Empty, add_generation_prompt: true);
            var sequences = _tokenizer.Encode(prompt);

            using var generatorParams = new GeneratorParams(_model);
            generatorParams.SetSearchOption("max_length", 4096);
            generatorParams.SetSearchOption("do_sample", false);
            using var generator = new Generator(_model, generatorParams);
            // Activate the configured adapter; leaving specialized weights idle would be amateur behavior.
            if (_adapters is not null && !string.IsNullOrWhiteSpace(_adapterName))
            {
                generator.SetActiveAdapter(_adapters, _adapterName);
            }

            generator.AppendTokenSequences(sequences);
            var output = new StringBuilder();
            var generatedTokens = 0;
            // Stop at completion, 384 tokens, or cancellation; infinity is impressive but bad for response times.
            while (!generator.IsDone() && generatedTokens < 384 && !cancellationToken.IsCancellationRequested)
            {
                generator.GenerateNextToken();
                output.Append(tokenizerStream.Decode(generator.GetSequence(0)[^1]));
                generatedTokens++;
            }

            return output.ToString();
        }

        public void Dispose()
        {
            // Dispose native handles in reverse order; memory exits as elegantly as it entered.
            _adapters?.Dispose();
            _tokenizer.Dispose();
            _model.Dispose();
            _ogaHandle.Dispose();
        }
    }
}