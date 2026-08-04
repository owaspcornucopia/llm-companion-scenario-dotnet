using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Companion.Api;

namespace Companion.Api.Tests;

public sealed class FakeModelServiceClientFactory : IHttpClientFactory
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    public void EnqueueResult(string result)
        => _responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["result"] = result }),
        });

    public void EnqueueError(HttpStatusCode statusCode, string error)
        => _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["error"] = error }),
        });

    public HttpClient CreateClient(string name)
        => new(new FakeModelHandler(this)) { BaseAddress = new Uri("http://localhost") };

    private HttpResponseMessage Dequeue(HttpRequestMessage request)
    {
        var json = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(json))
        {
            var payload = JsonSerializer.Deserialize<GenerateRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload?.Messages is not null)
            {
                Calls.Add(payload.Messages.Select(message => new ChatMessage(message.Role, message.Content)).ToList());
            }
        }

        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Dictionary<string, string> { ["result"] = string.Empty }),
            };
        }

        return _responses.Dequeue()(request);
    }

    private sealed class FakeModelHandler : HttpMessageHandler
    {
        private readonly FakeModelServiceClientFactory _owner;

        public FakeModelHandler(FakeModelServiceClientFactory owner)
        {
            _owner = owner;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_owner.Dequeue(request));
    }
}

public sealed class FakeOnnxTextGeneratorRuntimeFactory : IOnnxTextGeneratorRuntimeFactory
{
    private readonly string _result;
    private readonly Exception? _generateException;
    private readonly Exception? _createException;

    public FakeOnnxTextGeneratorRuntimeFactory(string result = "", Exception? generateException = null, Exception? createException = null)
    {
        _result = result;
        _generateException = generateException;
        _createException = createException;
    }

    public FakeOnnxTextGeneratorRuntime? LastRuntime { get; private set; }

    public IOnnxTextGeneratorRuntime Create(string modelPath)
        => Create(modelPath, null!, null!);

    public IOnnxTextGeneratorRuntime Create(string modelPath, string adapterPath, string adapterName)
    {
        if (_createException is not null)
        {
            throw _createException;
        }

        LastRuntime = new FakeOnnxTextGeneratorRuntime(_result, _generateException, modelPath, adapterPath, adapterName);
        return LastRuntime;
    }
}

public sealed class FakeOnnxTextGeneratorRuntime : IOnnxTextGeneratorRuntime
{
    private readonly string _result;
    private readonly Exception? _generateException;

    public FakeOnnxTextGeneratorRuntime(string result, Exception? generateException, string modelPath, string? adapterPath, string? adapterName)
    {
        _result = result;
        _generateException = generateException;
        ModelPath = modelPath;
        AdapterPath = adapterPath;
        AdapterName = adapterName;
    }

    public string ModelPath { get; }

    public string? AdapterPath { get; }

    public string? AdapterName { get; }

    public string? LastPrompt { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public bool IsDisposed { get; private set; }

    public string Generate(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        LastPrompt = RenderPrompt(messages);
        LastCancellationToken = cancellationToken;
        if (_generateException is not null)
        {
            throw _generateException;
        }

        return _result;
    }

    public void Dispose()
    {
        IsDisposed = true;
    }

    private static string RenderPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var message in messages)
        {
            builder.Append("<|")
                .Append(message.Role)
                .Append("|>\n")
                .Append(message.Content)
                .Append("<|end|>\n");
        }

        builder.Append("<|assistant|>\n");
        return builder.ToString();
    }
}
