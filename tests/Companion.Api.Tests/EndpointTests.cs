using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Companion.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Api.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task GenerateEndpointReturnsModelResult()
    {
        using var factory = new CompanionApiFactory("model", textGenerationService: CreateModelService(result: "answer"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/generate", new { messages = new[] { new { role = "user", content = "hi" } } });
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("answer", payload!["result"]);
    }

    [Fact]
    public async Task GenerateEndpointReturnsEmptyResult()
    {
        using var factory = new CompanionApiFactory("model", textGenerationService: CreateModelService());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/generate", new { });
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<EndpointTests>().LogError("Payload: {Payload}", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { ["result"] = "" }, payload);
    }

    [Fact]
    public async Task GenerateEndpointReturnsErrorWhenModelGenerationFails()
    {
        using var factory = new CompanionApiFactory("model", textGenerationService: CreateModelService(exception: new InvalidOperationException("boom")));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/generate", new { messages = new[] { new { role = "user", content = "hi" } } });
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(payload!["error"]));
    }

    [Fact]
    public async Task HealthReportsOkWhenModelIsAvailable()
    {
        using var factory = new CompanionApiFactory("model", textGenerationService: CreateModelService(result: "ready"));
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<Dictionary<string, JsonElement>>("/health");

        Assert.Equal("ok", payload!["status"].GetString());
        Assert.False(payload.ContainsKey("model_load_error"));
    }

    [Fact]
    public async Task ApiFraudSupportsGetRequests()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT * FROM investigations\"}}");
        modelClientFactory.EnqueueResult("answer");
        using var factory = new CompanionApiFactory("app", modelClientFactory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("token", "8a060bc7-e168-4a6c-bdd6-0df4a5822266");

        var response = await client.GetAsync("/api/fraud?question=Check%20it");
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var rows = payload!["response"].Deserialize<List<Dictionary<string, JsonElement>>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("answer", rows![^1]["Phi-3-mini"].GetString());
        Assert.Equal("Check it", modelClientFactory.Calls[0][1].Content);
    }

    [Fact]
    public async Task ApiFraudRequiresQuestion()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        using var factory = new CompanionApiFactory("app", modelClientFactory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/fraud");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiFraudReturnsToolGenerationFailureFromModelService()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueError(HttpStatusCode.InternalServerError, "boom");
        using var factory = new CompanionApiFactory("app", modelClientFactory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/fraud", new { question = "hello" });
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("boom", payload);
    }

    private static OnnxTextGenerationService CreateModelService(
        string result = "",
        Exception? exception = null,
        Exception? createException = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MODEL:BASEMODELPATH"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "base"),
            ["MODEL:ADAPTERPATH"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "adapter", "adapter_model.onnx_adapter"),
        }).Build();

        return new OnnxTextGenerationService(
            configuration,
            NullLogger<OnnxTextGenerationService>.Instance,
            new FakeOnnxTextGeneratorRuntimeFactory(result, exception, createException));
    }
}
