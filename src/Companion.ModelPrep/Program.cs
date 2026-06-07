using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

var options = ModelPrepOptions.Parse(args);
using var httpClient = HuggingFaceClientFactory.Create(options.Token);

Console.WriteLine("Preparing Phi-3 runtime artifacts outside the app startup path.");
Console.WriteLine($"Base repo        : {options.BaseModelRepo}");
Console.WriteLine($"Fine-tuned repo  : {options.FineTunedRepo}");
Console.WriteLine($"Base output      : {options.BaseModelPath}");
Console.WriteLine($"Adapter metadata : {options.AdapterPath}");
Console.WriteLine($"ONNX runtime dir : {options.OnnxOutputPath}");
Console.WriteLine("This prep step stays inside .NET and does not invoke Python tooling.");

Directory.CreateDirectory(options.BaseModelPath);
Directory.CreateDirectory(options.AdapterPath);
Directory.CreateDirectory(options.OnnxOutputPath);

await DownloadRepositorySnapshotAsync(httpClient, options.BaseModelRepo, options.BaseModelPath, options.Force);
await DownloadRepositorySnapshotAsync(httpClient, options.FineTunedRepo, options.OnnxOutputPath, options.Force);
await WriteAdapterMetadataAsync(options);

Console.WriteLine("Model preparation completed.");

static async Task DownloadRepositorySnapshotAsync(HttpClient httpClient, string repoId, string outputDirectory, bool force)
{
    var repository = await GetRepositoryAsync(httpClient, repoId);
    var files = repository.Siblings
        .Where(file => !string.IsNullOrWhiteSpace(file.RelativeFileName))
        .Where(file => !string.Equals(file.RelativeFileName, ".gitattributes", StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => file.RelativeFileName, StringComparer.Ordinal)
        .ToArray();

    if (files.Length == 0)
    {
        throw new InvalidOperationException($"Repository '{repoId}' does not contain any downloadable files.");
    }

    Console.WriteLine($"Downloading {files.Length} files from {repoId}...");

    foreach (var file in files)
    {
        var relativePath = file.RelativeFileName!;
        var localPath = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        if (!force && File.Exists(localPath) && new FileInfo(localPath).Length > 0)
        {
            Console.WriteLine($"  Skipping existing file: {relativePath}");
            continue;
        }

        var downloadUrl = $"https://huggingface.co/{repoId}/resolve/main/{relativePath}";
        Console.WriteLine($"  Downloading: {relativePath}");
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"File '{relativePath}' was not found in repository '{repoId}'.");
        }

        response.EnsureSuccessStatusCode();

        await using var remoteStream = await response.Content.ReadAsStreamAsync();
        await using var localStream = File.Create(localPath);
        await remoteStream.CopyToAsync(localStream);
    }
}

static async Task<HuggingFaceRepository> GetRepositoryAsync(HttpClient httpClient, string repoId)
{
    using var response = await httpClient.GetAsync($"https://huggingface.co/api/models/{repoId}");

    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        throw new InvalidOperationException($"Repository '{repoId}' was not found on Hugging Face.");
    }

    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        throw new InvalidOperationException($"Repository '{repoId}' requires authentication. Set HF_TOKEN before running the prep tool.");
    }

    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync();
    var repository = await JsonSerializer.DeserializeAsync<HuggingFaceRepository>(stream, JsonDefaults.Options);
    return repository ?? throw new InvalidOperationException($"Repository metadata for '{repoId}' could not be parsed.");
}

static async Task WriteAdapterMetadataAsync(ModelPrepOptions options)
{
    var metadata = new AdapterMetadata(
        options.BaseModelRepo,
        options.FineTunedRepo,
        "The fine-tuned Hugging Face ONNX package is already merged for runtime use. This directory only preserves the base/adapter/onnx layout from the original scenario.",
        DateTimeOffset.UtcNow);

    var metadataPath = Path.Combine(options.AdapterPath, "adapter-info.json");
    await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonDefaults.Options));
}

internal sealed record ModelPrepOptions(
    string BaseModelRepo,
    string FineTunedRepo,
    string BaseModelPath,
    string AdapterPath,
    string OnnxOutputPath,
    string? Token,
    bool Force)
{
    public static ModelPrepOptions Parse(string[] args)
    {
        var values = args
            .Select(argument => argument.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("--", StringComparison.Ordinal))
            .ToDictionary(parts => parts[0][2..], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        return new ModelPrepOptions(
            values.GetValueOrDefault("base-model-repo") ?? "microsoft/Phi-3-mini-4k-instruct-onnx",
            values.GetValueOrDefault("fine-tuned-repo") ?? "steephole5586/pwnednext-dotnet",
            values.GetValueOrDefault("base-model-path") ?? Path.Combine("models", "base", "Phi-3-mini-4k-instruct-onnx"),
            values.GetValueOrDefault("adapter-path") ?? Path.Combine("models", "adapters", "pwnednext-dotnet"),
            values.GetValueOrDefault("onnx-output-path") ?? Path.Combine("models", "onnx", "pwnednext-dotnet"),
            values.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("HF_TOKEN"),
            values.ContainsKey("force"));
    }
}

internal static class HuggingFaceClientFactory
{
    public static HttpClient Create(string? token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("llm-companion-scenario-dotnet-modelprep/1.0");

        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}

internal sealed record HuggingFaceRepository(HuggingFaceSibling[] Siblings);

internal sealed record HuggingFaceSibling([property: JsonPropertyName("rfilename")] string? RelativeFileName);

internal sealed record AdapterMetadata(
    string BaseModelRepo,
    string FineTunedRepo,
    string Notes,
    DateTimeOffset PreparedAtUtc);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}