using Companion.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Api.Tests;

public sealed class ServiceTests
{
    [Fact]
    public void ParseToolCallAcceptsSupportedFormats()
    {
        var parser = CreateOrchestrator();
        var cases = new[]
        {
            ("```json\n{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT 1\"}}\n```", "SELECT 1"),
            ("Use this {\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT 2\"}} now", "SELECT 2"),
            ("{'tool': 'investigation_fraud', 'args': {'query': 'SELECT 3'}}", "SELECT 3"),
            ("{\"tool\": \"investigation_fraud\", \"args\": \"{\\\"query\\\": \\\"SELECT 5\\\"}\"}", "SELECT 5"),
            ("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT *\nFROM investigations\"}}", "SELECT * FROM investigations"),
            ("SELECT * FROM investigations", "SELECT * FROM investigations"),
        };

        foreach (var (text, expectedQuery) in cases)
        {
            Assert.Equal(new ToolCall("investigation_fraud", new ToolCallArgs(expectedQuery)), parser.ParseToolCall(text));
        }
    }

    [Fact]
    public void ParseToolCallRejectsInvalidPayloads()
    {
        var parser = CreateOrchestrator();
        var cases = new[]
        {
            "{\"tool\":\"different_tool\",\"args\":{\"query\":\"SELECT 1\"}}",
            "{\"tool\":\"investigation_fraud\"}",
            "{\"tool\":\"investigation_fraud\",\"args\":{}}",
            "{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"   \"}}",
            "{\"tool\":\"investigation_fraud\",\"args\":\"not a dict\"}",
            "not valid output",
        };

        foreach (var text in cases)
        {
            Assert.Null(parser.ParseToolCall(text));
        }
    }

    [Fact]
    public void IsAllowedRejectsMissingTokenAndAcceptsKnownToken()
    {
        var orchestrator = CreateOrchestrator();

        Assert.False(orchestrator.IsAllowed(null));
        Assert.True(orchestrator.IsAllowed("8a060bc7-e168-4a6c-bdd6-0df4a5822266"));
    }

    [Fact]
    public void OrchestratorReportsAvailabilityWithoutModelLoadError()
    {
        var orchestrator = CreateOrchestrator();

        Assert.True(orchestrator.IsAvailable);
        Assert.Null(orchestrator.ModelLoadError);
    }

    [Fact]
    public void RoundTripPreservesTokens()
    {
        var tokens = new[] { "one", "two" };
        var roundTripped = FraudWorkflowOrchestrator.RoundTrip(tokens);

        Assert.Equal(tokens, roundTripped);
    }

    [Fact]
    public void OnnxTextGenerationServiceThrowsWhenModelLoadFails()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CreateOnnxService(createException: new InvalidOperationException("offline")));

        Assert.Equal("offline", exception.Message);
    }

    [Fact]
    public async Task OnnxTextGenerationServiceGenerateOnceAsyncBuildsPromptAndTrimsOutput()
    {
        var runtimeFactory = new FakeOnnxTextGeneratorRuntimeFactory("  done  ");
        var service = CreateOnnxService(runtimeFactory: runtimeFactory);
        var messages = new[]
        {
            new ChatMessage("system", OnnxTextGenerationService.SystemPromptSql),
            new ChatMessage("user", "hello"),
        };

        var result = await service.GenerateOnceAsync(messages, CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal("<|system|>\n" + OnnxTextGenerationService.SystemPromptSql + "<|end|>\n<|user|>\nhello<|end|>\n<|assistant|>\n", runtimeFactory.LastRuntime!.LastPrompt);
    }

    [Fact]
    public async Task OnnxTextGenerationServiceGenerateOnceAsyncPropagatesRuntimeFailure()
    {
        var service = CreateOnnxService(runtimeFactory: new FakeOnnxTextGeneratorRuntimeFactory(generateException: new InvalidOperationException("generate failed")));
        var messages = new[]
        {
            new ChatMessage("user", "hello"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateOnceAsync(messages, CancellationToken.None));
    }

    [Fact]
    public void OnnxTextGenerationServiceDisposeDisposesRuntime()
    {
        var runtimeFactory = new FakeOnnxTextGeneratorRuntimeFactory();
        var service = CreateOnnxService(runtimeFactory: runtimeFactory);

        service.Dispose();

        Assert.True(runtimeFactory.LastRuntime!.IsDisposed);
    }

    [Fact]
    public async Task InvestigateTransactionOrchestratesToolAndFinalAnswer()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT * FROM investigations WHERE fraud_detected='true'\"}}");
        modelClientFactory.EnqueueResult("likely fraudulent");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("Is this fraudulent?", "8a060bc7-e168-4a6c-bdd6-0df4a5822266", CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("likely fraudulent", response[^1]["Phi-3-mini"]);
        Assert.Equal(2, modelClientFactory.Calls.Count);
        Assert.Equal("system", modelClientFactory.Calls[0][0].Role);
        Assert.Contains("Tool execution result:", modelClientFactory.Calls[1][2].Content);
        Assert.Contains("74c9a7e9-e30c-48f0-8d8f-ec8771849d46", modelClientFactory.Calls[1][2].Content);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task InvestigateTransactionHandlesToolCallGenerationFailure()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueError(System.Net.HttpStatusCode.InternalServerError, "model offline");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("hello", null, CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("I could not generate an investigation tool call.", response[0]["Phi-3-mini"]);
        Assert.Equal("model offline", response[0]["error"]);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task InvestigateTransactionHandlesInvalidToolCall()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("nonsense");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("hello", null, CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Tool output format did not match expected schema.", response[0]["error"]);
        Assert.Equal("nonsense", response[0]["raw_output"]);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task InvestigateTransactionHandlesToolExecutionFailure()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT * FROM does_not_exist\"}}");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("hello", "8a060bc7-e168-4a6c-bdd6-0df4a5822266", CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("Investigation tool execution failed.", response[0]["Phi-3-mini"]);
        Assert.Equal("SELECT * FROM does_not_exist", response[0]["sql_query"]);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task InvestigateTransactionRejectsToolExecutionWithoutToken()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT * FROM investigations\"}}");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("hello", null, CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("Investigation tool execution failed.", response[0]["Phi-3-mini"]);
        Assert.Equal("You need a token", response[0]["error"]);
        Assert.Equal("SELECT * FROM investigations", response[0]["sql_query"]);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task InvestigateTransactionHandlesFinalAnswerGenerationFailure()
    {
        var modelClientFactory = new FakeModelServiceClientFactory();
        modelClientFactory.EnqueueResult("{\"tool\":\"investigation_fraud\",\"args\":{\"query\":\"SELECT 1\"}}");
        modelClientFactory.EnqueueError(System.Net.HttpStatusCode.InternalServerError, "answer failed");

        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);
        await seeder.SetupDbAsync(CancellationToken.None);

        var orchestrator = CreateOrchestrator(configuration, modelClientFactory);

        var result = await orchestrator.InvestigateTransactionAsync("hello", "8a060bc7-e168-4a6c-bdd6-0df4a5822266", CancellationToken.None);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Payload);
        var response = Assert.IsType<List<Dictionary<string, object?>>>(payload["response"]);

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("Final answer generation failed.", response[^1]["Phi-3-mini"]);
        Assert.Equal("answer failed", response[^1]["error"]);

        TryDelete(databasePath);
    }

    [Fact]
    public async Task SetupDbCreatesSampleRowsAndReplacesExistingFile()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        await File.WriteAllTextAsync(databasePath, "stale");
        var configuration = CreateConfiguration(databasePath);
        var seeder = new DatabaseSeeder(configuration);

        await seeder.SetupDbAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM investigations";
        var rows = (long)(await command.ExecuteScalarAsync() ?? 0L);

        Assert.Equal(2, rows);

        TryDelete(databasePath);
    }

    private static IConfiguration CreateConfiguration(string databasePath)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DB_CONNECTION_STRING"] = databasePath,
        }).Build();

    private static FraudWorkflowOrchestrator CreateOrchestrator(
        IConfiguration? configuration = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        configuration ??= CreateConfiguration(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite"));
        httpClientFactory ??= new FakeModelServiceClientFactory();
        return new FraudWorkflowOrchestrator(
            NullLogger<FraudWorkflowOrchestrator>.Instance,
            configuration,
            httpClientFactory);
    }

    private static OnnxTextGenerationService CreateOnnxService(
        IConfiguration? configuration = null,
        FakeOnnxTextGeneratorRuntimeFactory? runtimeFactory = null,
        Exception? createException = null)
    {
        configuration ??= new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MODEL:BASEMODELPATH"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "base"),
            ["MODEL:ADAPTERPATH"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "adapter"),
        }).Build();

        runtimeFactory ??= new FakeOnnxTextGeneratorRuntimeFactory(createException: createException);
        return new OnnxTextGenerationService(configuration, NullLogger<OnnxTextGenerationService>.Instance, runtimeFactory);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
