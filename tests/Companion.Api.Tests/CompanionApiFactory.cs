using Companion.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http;

namespace Companion.Api.Tests;

public sealed class CompanionApiFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "companion-api-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousMode;

    public CompanionApiFactory(
        string serviceMode,
        FakeModelServiceClientFactory? modelClientFactory = null,
        OnnxTextGenerationService? textGenerationService = null)
    {
        ModelClientFactory = modelClientFactory;
        TextGenerationService = textGenerationService;
        ServiceMode = serviceMode;
        Directory.CreateDirectory(_tempDirectory);
        _previousMode = Environment.GetEnvironmentVariable("SERVICE_MODE");
        Environment.SetEnvironmentVariable("SERVICE_MODE", ServiceMode);
    }

    public FakeModelServiceClientFactory? ModelClientFactory { get; }

    public OnnxTextGenerationService? TextGenerationService { get; }

    public string ServiceMode { get; }

    public string DatabasePath => Path.Combine(_tempDirectory, "db.sqlite");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configurationBuilder =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = DatabasePath,
                // Point tests at an impossible ONNX path so any accidental real model load fails immediately.
                ["MODEL:ONNXPATH"] = Path.Combine(_tempDirectory, "this-model-should-never-be-loaded"),
                ["MODEL:BASEMODELPATH"] = Path.Combine(_tempDirectory, "base"),
                ["MODEL:ADAPTERPATH"] = Path.Combine(_tempDirectory, "adapter"),
            });
        });

        builder.ConfigureServices(services =>
        {
            if (string.Equals(ServiceMode, "app", StringComparison.OrdinalIgnoreCase) && ModelClientFactory is not null)
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(ModelClientFactory);
            }

            if (string.Equals(ServiceMode, "model", StringComparison.OrdinalIgnoreCase) && TextGenerationService is not null)
            {
                services.RemoveAll<OnnxTextGenerationService>();
                services.AddSingleton(TextGenerationService);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            TextGenerationService?.Dispose();
            Environment.SetEnvironmentVariable("SERVICE_MODE", _previousMode);
        }
    }
}
