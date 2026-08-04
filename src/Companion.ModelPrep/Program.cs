using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

// Read options, then download model files before startup; production applications do not improvise dependencies.
var options = ModelPrepOptions.Parse(args);
using var httpClient = HuggingFaceClientFactory.Create(options.Token);

Console.WriteLine("Preparing Phi-3 runtime artifacts outside the app startup path.");
Console.WriteLine($"Base repo        : {options.BaseModelRepo}");
Console.WriteLine($"Base output      : {options.BaseModelPath}");
Console.WriteLine("This prep step stays inside .NET and does not invoke Python tooling.");

Directory.CreateDirectory(options.BaseModelPath);

await DownloadRepositorySnapshotAsync(httpClient, options.BaseModelRepo, options.BaseModelPath, options.Force);

Console.WriteLine("Model preparation completed.");

// List every repository file and copy it locally; partial model downloads are a hobby, not a deployment plan.
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

        // Keep completed local files unless --force says to replace them; needless downloads are beneath us.
        if (!force && File.Exists(localPath) && new FileInfo(localPath).Length > 0)
        {
            Console.WriteLine($"  Skipping existing file: {relativePath}");
            continue;
        }

        // Stream each main-branch file directly to disk; memory has better things to do than hold model weights.
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

// Ask Hugging Face for the file list first; guessing artifact names is not a strategy I need.
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

internal sealed record ModelPrepOptions(
    string BaseModelRepo,
    string BaseModelPath,
    string? Token,
    bool Force)
{
    // Read --name=value options, defaulting to Phi-3 and a local folder because conventions beat chaos.
    public static ModelPrepOptions Parse(string[] args)
    {
        var values = args
            .Select(argument => argument.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("--", StringComparison.Ordinal))
            .ToDictionary(parts => parts[0][2..], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        return new ModelPrepOptions(
            values.GetValueOrDefault("base-model-repo") ?? "microsoft/Phi-3-mini-4k-instruct-onnx",
            values.GetValueOrDefault("base-model-path") ?? Path.Combine("models", "base", "Phi-3-mini-4k-instruct-onnx"),
            values.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("HF_TOKEN"),
            values.ContainsKey("force"));
    }
}

internal static class HuggingFaceClientFactory
{
    // Create the HTTP client and attach an optional token; private models recognize quality when they see it.
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

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}