using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vela.Backend;

/// <summary>Thrown when packing, authentication, or a registry interaction fails.</summary>
public sealed class VelaRegistryException(string message) : Exception(message)
{
}

/// <summary>Describes one file captured inside a <c>.vpkg</c> archive.</summary>
public sealed record VelaPackageArchiveFile(string Path, string Sha256);

/// <summary>Represents the <c>vpkg.json</c> metadata document embedded in every archive.</summary>
public sealed record VelaPackageArchiveManifest(
    int FormatVersion,
    string Name,
    string Version,
    string Kind,
    IReadOnlyList<VelaPackageArchiveFile> Files);

/// <summary>Creates distributable <c>.vpkg</c> archives from local Vela packages.</summary>
public static class VelaPackageArchive
{
    /// <summary>The current <c>.vpkg</c> format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The file extension used by Vela package archives.</summary>
    public const string Extension = ".vpkg";

    // Zip timestamps cannot represent dates before 1980; a fixed stamp keeps archives reproducible.
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Regex SemVerPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>Packs the package at <paramref name="packagePath"/> into a <c>.vpkg</c> archive.</summary>
    /// <param name="packagePath">A package directory or explicit <c>vela.toml</c> path.</param>
    /// <param name="versionOverride">An optional SemVer 2.0 version replacing the manifest version.</param>
    /// <param name="outputDirectory">The directory receiving the archive; defaults to <c>artifacts</c> under the package root.</param>
    /// <returns>The full path of the created archive.</returns>
    public static string Pack(string packagePath, string? versionOverride = null, string? outputDirectory = null)
    {
        var manifest = VelaPackageResolver.LoadManifest(packagePath);
        var version = versionOverride ?? manifest.Version;
        if (!SemVerPattern.IsMatch(version))
        {
            throw new VelaRegistryException($"Version '{version}' is not a valid SemVer 2.0 version (expected e.g. 1.0.0 or 1.2.3-beta.1).");
        }

        var files = CollectFiles(manifest.RootDirectory, manifest.ManifestPath);
        var archiveManifest = new VelaPackageArchiveManifest(
            FormatVersion,
            manifest.Name,
            version,
            FormatKind(manifest.Kind),
            files.Select(file => new VelaPackageArchiveFile(
                file.RelativePath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullPath))).ToLowerInvariant()))
                .ToArray());

        var destination = Path.GetFullPath(outputDirectory ?? Path.Combine(manifest.RootDirectory, "artifacts"));
        Directory.CreateDirectory(destination);
        var archivePath = Path.Combine(destination, $"{manifest.Name}.{version}{Extension}");

        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using var entryStream = entry.Open();
            using var source = File.OpenRead(file.FullPath);
            source.CopyTo(entryStream);
        }

        var metadataEntry = archive.CreateEntry("vpkg.json", CompressionLevel.Optimal);
        metadataEntry.LastWriteTime = ArchiveTimestamp;
        using (var metadataStream = metadataEntry.Open())
        {
            JsonSerializer.Serialize(metadataStream, archiveManifest, VelaJsonSerializerContext.Default.VelaPackageArchiveManifest);
        }

        return archivePath;
    }

    /// <summary>Reads the embedded <c>vpkg.json</c> metadata from an archive.</summary>
    /// <param name="archivePath">The <c>.vpkg</c> file to inspect.</param>
    /// <returns>The embedded archive manifest.</returns>
    public static VelaPackageArchiveManifest ReadManifest(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new VelaRegistryException($"Package archive '{archivePath}' was not found.");
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("vpkg.json")
            ?? throw new VelaRegistryException($"'{archivePath}' is not a Vela package archive: vpkg.json is missing.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize(stream, VelaJsonSerializerContext.Default.VelaPackageArchiveManifest)
            ?? throw new VelaRegistryException($"'{archivePath}' contains an empty vpkg.json.");
    }

    private static List<(string FullPath, string RelativePath)> CollectFiles(string rootDirectory, string manifestPath)
    {
        var files = new List<(string, string)> { (manifestPath, "vela.toml") };
        var sourceDirectory = Path.Combine(rootDirectory, "src");
        if (!Directory.Exists(sourceDirectory))
        {
            throw new VelaRegistryException($"Package '{rootDirectory}' has no src directory to pack.");
        }

        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*.vela", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files.Add((source, Path.GetRelativePath(rootDirectory, source).Replace('\\', '/')));
        }

        foreach (var optional in new[] { "README.md", "LICENSE", "LICENSE.md", "LICENSE.txt" })
        {
            var candidate = Path.Combine(rootDirectory, optional);
            if (File.Exists(candidate))
            {
                files.Add((candidate, optional));
            }
        }

        return files;
    }

    private static string FormatKind(VelaPackageKind kind) => kind switch
    {
        VelaPackageKind.Application => "application",
        VelaPackageKind.Library => "library",
        VelaPackageKind.SourceLibrary => "source-library",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>Represents one stored registry credential.</summary>
public sealed record VelaRegistryCredential(string Source, string ApiKey);

/// <summary>Represents the on-disk credential document.</summary>
public sealed record VelaRegistryCredentialsDocument(int Version, IReadOnlyList<VelaRegistryCredential> Sources);

/// <summary>Stores registry API keys per source under the user profile.</summary>
public sealed class VelaRegistryCredentialStore
{
    private readonly string _path;

    /// <summary>Initializes a store rooted at the default per-user location.</summary>
    public VelaRegistryCredentialStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vela", "credentials.json"))
    {
    }

    /// <summary>Initializes a store rooted at an explicit file path (used by tests).</summary>
    /// <param name="path">The credential file path.</param>
    public VelaRegistryCredentialStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Gets the credential file path used by this store.</summary>
    public string Location => _path;

    /// <summary>Saves or replaces the API key for <paramref name="source"/>.</summary>
    /// <param name="source">The registry source URL.</param>
    /// <param name="apiKey">The API key to store.</param>
    public void Save(string source, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var normalized = NormalizeSource(source);
        var entries = Load().Sources
            .Where(entry => !string.Equals(entry.Source, normalized, StringComparison.OrdinalIgnoreCase))
            .Append(new VelaRegistryCredential(normalized, apiKey))
            .OrderBy(static entry => entry.Source, StringComparer.Ordinal)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var content = JsonSerializer.Serialize(
            new VelaRegistryCredentialsDocument(1, entries),
            VelaJsonSerializerContext.Default.VelaRegistryCredentialsDocument) + Environment.NewLine;
        File.WriteAllText(_path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Looks up the stored API key for <paramref name="source"/>.</summary>
    /// <param name="source">The registry source URL.</param>
    /// <returns>The API key, or <see langword="null"/> when no credential is stored.</returns>
    public string? Find(string source)
    {
        var normalized = NormalizeSource(source);
        return Load().Sources
            .FirstOrDefault(entry => string.Equals(entry.Source, normalized, StringComparison.OrdinalIgnoreCase))
            ?.ApiKey;
    }

    /// <summary>Removes the stored API key for <paramref name="source"/>.</summary>
    /// <param name="source">The registry source URL.</param>
    /// <returns><see langword="true"/> when a credential was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(string source)
    {
        var normalized = NormalizeSource(source);
        var document = Load();
        var remaining = document.Sources
            .Where(entry => !string.Equals(entry.Source, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == document.Sources.Count)
        {
            return false;
        }

        var content = JsonSerializer.Serialize(
            new VelaRegistryCredentialsDocument(1, remaining),
            VelaJsonSerializerContext.Default.VelaRegistryCredentialsDocument) + Environment.NewLine;
        File.WriteAllText(_path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    /// <summary>Normalizes a source URL for stable comparison and storage.</summary>
    /// <param name="source">The registry source URL.</param>
    /// <returns>The normalized source.</returns>
    public static string NormalizeSource(string source) => source.Trim().TrimEnd('/');

    private VelaRegistryCredentialsDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new VelaRegistryCredentialsDocument(1, []);
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, VelaJsonSerializerContext.Default.VelaRegistryCredentialsDocument)
                ?? new VelaRegistryCredentialsDocument(1, []);
        }
        catch (JsonException)
        {
            throw new VelaRegistryException($"Credential file '{_path}' is corrupted. Delete it and run 'vela package login' again.");
        }
    }
}

/// <summary>Describes the result of a registry push.</summary>
public sealed record VelaRegistryPushResult(int StatusCode, string Message);

/// <summary>Uploads <c>.vpkg</c> archives to a Vela package registry.</summary>
public sealed class VelaRegistryClient(HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>The default registry service index, matching the public Vela gallery.</summary>
    public const string DefaultSource = "https://packages.vela.dev/v3/index.json";

    /// <summary>The request header carrying the publisher API key.</summary>
    public const string ApiKeyHeader = "X-Vela-ApiKey";

    private readonly HttpClient _http = handler is null
        ? new HttpClient { Timeout = TimeSpan.FromSeconds(100) }
        : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>Resolves the publish endpoint for <paramref name="source"/>.</summary>
    /// <remarks>
    /// A source ending in <c>.json</c> is treated as a NuGet-style service index whose
    /// <c>resources</c> array advertises a <c>PackagePublish</c> entry. Any other source
    /// is used directly as the publish endpoint.
    /// </remarks>
    /// <param name="source">The registry source URL.</param>
    /// <param name="cancellationToken">Cancels the index request.</param>
    /// <returns>The absolute publish endpoint URL.</returns>
    public async Task<string> ResolvePublishEndpointAsync(string source, CancellationToken cancellationToken = default)
    {
        var normalized = VelaRegistryCredentialStore.NormalizeSource(source);
        if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        string payload;
        try
        {
            payload = await _http.GetStringAsync(new Uri(normalized), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new VelaRegistryException($"Unable to read the service index at {normalized}: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VelaRegistryException($"The service index at {normalized} did not respond in time.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                foreach (var resource in resources.EnumerateArray())
                {
                    if (resource.TryGetProperty("@type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString()!.StartsWith("PackagePublish", StringComparison.OrdinalIgnoreCase)
                        && resource.TryGetProperty("@id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                    {
                        return VelaRegistryCredentialStore.NormalizeSource(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            throw new VelaRegistryException($"The service index at {normalized} is not valid JSON.");
        }

        throw new VelaRegistryException($"The service index at {normalized} does not advertise a PackagePublish resource.");
    }

    /// <summary>Uploads the archive at <paramref name="archivePath"/> to <paramref name="publishEndpoint"/>.</summary>
    /// <param name="archivePath">The <c>.vpkg</c> file to upload.</param>
    /// <param name="publishEndpoint">The absolute publish endpoint URL.</param>
    /// <param name="apiKey">The publisher API key.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The registry response summary.</returns>
    public async Task<VelaRegistryPushResult> PushAsync(
        string archivePath,
        string publishEndpoint,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var manifest = VelaPackageArchive.ReadManifest(archivePath);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(publishEndpoint));
        request.Headers.Add(ApiKeyHeader, apiKey);
        using var content = new MultipartFormDataContent();
        await using var stream = File.OpenRead(archivePath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "package", Path.GetFileName(archivePath));
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new VelaRegistryException($"Unable to reach the registry at {publishEndpoint}: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VelaRegistryException($"The registry at {publishEndpoint} did not respond in time.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            return status switch
            {
                200 or 201 or 202 => new VelaRegistryPushResult(status, $"{manifest.Name} v{manifest.Version} accepted by the registry."),
                401 or 403 => throw new VelaRegistryException("The registry rejected the API key. Run 'vela package login' with a valid key from your publisher profile."),
                409 => throw new VelaRegistryException($"{manifest.Name} v{manifest.Version} already exists in the registry. Bump the version and pack again."),
                413 => throw new VelaRegistryException("The registry rejected the archive because it is too large."),
                _ => throw new VelaRegistryException($"The registry returned HTTP {status} for {manifest.Name} v{manifest.Version}.")
            };
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
