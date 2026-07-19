using System.Net.Http.Headers;
using System.Text.Json;
using Vela.Packages.Tuf;

namespace Vela.Packages;

/// <summary>Thrown when registry restore fails.</summary>
public sealed class VelaRegistryRestoreException(string message) : Exception(message);

/// <summary>Result of restoring one package from a registry.</summary>
public sealed record VelaRestoredPackage(
    string Name,
    string Version,
    string Path,
    VelaPackageManifest Manifest);

/// <summary>MVP registry client supporting local file registries and HTTP metadata download.</summary>
public sealed class VelaRestoreClient : IDisposable
{
    private readonly VelaPackageCache _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>Initializes a client with the default cache location.</summary>
    public VelaRestoreClient()
        : this(new VelaPackageCache(), handler: null)
    {
    }

    /// <summary>Initializes a client with an explicit cache and optional HTTP handler.</summary>
    public VelaRestoreClient(VelaPackageCache cache, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        if (handler is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = true;
        }
    }

    /// <summary>Gets the package cache used by this client.</summary>
    public VelaPackageCache Cache => _cache;

    /// <summary>Restores <paramref name="packageName"/> matching <paramref name="versionRange"/> from <paramref name="registryUrl"/>.</summary>
    public async Task<VelaRestoredPackage> RestoreAsync(
        string packageName,
        string versionRange,
        string registryUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryUrl);

        if (!Uri.TryCreate(registryUrl, UriKind.Absolute, out var registryUri))
        {
            throw new VelaRegistryRestoreException($"Registry URL '{registryUrl}' is not a valid absolute URI.");
        }

        return registryUri.Scheme switch
        {
            "file" => await RestoreFromFileRegistryAsync(packageName, versionRange, registryUri, cancellationToken).ConfigureAwait(false),
            "http" or "https" => await RestoreFromHttpRegistryAsync(packageName, versionRange, registryUri, cancellationToken).ConfigureAwait(false),
            _ => throw new VelaRegistryRestoreException($"Registry scheme '{registryUri.Scheme}' is not supported.")
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private async Task<VelaRestoredPackage> RestoreFromFileRegistryAsync(
        string packageName,
        string versionRange,
        Uri registryUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = registryUri.LocalPath;
        if (!Directory.Exists(root))
        {
            throw new VelaRegistryRestoreException($"File registry root '{root}' does not exist.");
        }

        var packageRoot = Path.Combine(root, packageName);
        if (!Directory.Exists(packageRoot))
        {
            throw new VelaRegistryRestoreException($"Package '{packageName}' was not found in file registry '{root}'.");
        }

        var version = ResolveFileRegistryVersion(packageRoot, versionRange);
        var versionDirectory = Path.Combine(packageRoot, version.ToString());
        if (!Directory.Exists(versionDirectory))
        {
            throw new VelaRegistryRestoreException($"Version '{version}' of '{packageName}' was not found in file registry.");
        }

        if (_cache.HasExtracted(packageName, version.ToString()))
        {
            var cachedPath = _cache.GetPackagePath(packageName, version.ToString());
            var cachedManifest = VelaPackageManifest.FromToml(Path.Combine(cachedPath, "vela.toml"));
            return new VelaRestoredPackage(packageName, version.ToString(), cachedPath, cachedManifest);
        }

        var archivePath = Path.Combine(versionDirectory, $"package{VelaDependencyArchive.Extension}");
        string extractedPath;
        VelaPackageManifest manifest;

        if (File.Exists(archivePath))
        {
            var tempExtract = Path.Combine(Path.GetTempPath(), "vela-restore", Guid.NewGuid().ToString("N"));
            try
            {
                VerifyArchiveIfTufPresent(root, packageName, version.ToString(), archivePath);
                extractedPath = VelaDependencyArchive.Unpack(archivePath, tempExtract);
                manifest = VelaPackageManifest.FromToml(Path.Combine(extractedPath, "vela.toml"));
                var cached = _cache.StoreExtracted(packageName, version.ToString(), extractedPath);
                return new VelaRestoredPackage(packageName, version.ToString(), cached, manifest);
            }
            finally
            {
                if (Directory.Exists(tempExtract))
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
            }
        }

        var manifestPath = Path.Combine(versionDirectory, "vela.toml");
        if (!File.Exists(manifestPath))
        {
            throw new VelaRegistryRestoreException(
                $"Version '{version}' of '{packageName}' has neither package{VelaDependencyArchive.Extension} nor vela.toml.");
        }

        manifest = VelaPackageManifest.FromToml(manifestPath);
        extractedPath = _cache.StoreExtracted(packageName, version.ToString(), versionDirectory);
        return new VelaRestoredPackage(packageName, version.ToString(), extractedPath, manifest);
    }

    private async Task<VelaRestoredPackage> RestoreFromHttpRegistryAsync(
        string packageName,
        string versionRange,
        Uri registryUri,
        CancellationToken cancellationToken)
    {
        var version = await ResolveHttpRegistryVersionAsync(packageName, versionRange, registryUri, cancellationToken).ConfigureAwait(false);
        var versionText = version.ToString();

        if (_cache.HasExtracted(packageName, versionText))
        {
            var cachedPath = _cache.GetPackagePath(packageName, versionText);
            var cachedManifest = VelaPackageManifest.FromToml(Path.Combine(cachedPath, "vela.toml"));
            return new VelaRestoredPackage(packageName, versionText, cachedPath, cachedManifest);
        }

        var manifestUrl = new Uri(registryUri, $"packages/{packageName}/{versionText}/manifest.json");
        var manifestJson = await DownloadStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(manifestJson, VelaPackageJsonContext.Default.VelaPackageManifest)
            ?? throw new VelaRegistryRestoreException($"Manifest at '{manifestUrl}' is empty.");

        var archiveUrl = new Uri(registryUri, $"packages/{packageName}/{versionText}/package{VelaDependencyArchive.Extension}");
        var tempArchive = Path.Combine(Path.GetTempPath(), "vela-restore", $"{Guid.NewGuid():N}{VelaDependencyArchive.Extension}");
        var tempExtract = Path.Combine(Path.GetTempPath(), "vela-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tempArchive)!);

        try
        {
            await DownloadFileAsync(archiveUrl, tempArchive, cancellationToken).ConfigureAwait(false);
            await VerifyArchiveIfTufPresentAsync(registryUri, packageName, versionText, tempArchive, cancellationToken)
                .ConfigureAwait(false);
            _cache.StoreArchive(packageName, versionText, tempArchive);
            var extractedPath = VelaDependencyArchive.Unpack(tempArchive, tempExtract);
            var cached = _cache.StoreExtracted(packageName, versionText, extractedPath);
            return new VelaRestoredPackage(packageName, versionText, cached, manifest);
        }
        finally
        {
            if (File.Exists(tempArchive))
            {
                File.Delete(tempArchive);
            }

            if (Directory.Exists(tempExtract))
            {
                Directory.Delete(tempExtract, recursive: true);
            }
        }
    }

    private static SemVer ResolveFileRegistryVersion(string packageRoot, string versionRange)
    {
        var versions = Directory.EnumerateDirectories(packageRoot)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(name => SemVer.TryParse(name!, out var parsed) ? parsed : null)
            .Where(static version => version is not null)
            .Cast<SemVer>()
            .Where(version => SemVer.Satisfies(version, versionRange))
            .OrderDescending()
            .ToList();

        if (versions.Count == 0)
        {
            throw new VelaRegistryRestoreException($"No version of package in '{packageRoot}' satisfies '{versionRange}'.");
        }

        return versions[0];
    }

    private async Task<SemVer> ResolveHttpRegistryVersionAsync(
        string packageName,
        string versionRange,
        Uri registryUri,
        CancellationToken cancellationToken)
    {
        if (SemVer.TryParse(versionRange.Trim().TrimStart('^', '~'), out var exact)
            && !versionRange.Contains('*', StringComparison.Ordinal)
            && versionRange is not "latest")
        {
            var manifestUrl = new Uri(registryUri, $"packages/{packageName}/{exact!.ToString()}/manifest.json");
            try
            {
                _ = await DownloadStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
                return exact;
            }
            catch (VelaRegistryRestoreException)
            {
                if (versionRange is not "*" and not "latest" && !versionRange.StartsWith('^') && !versionRange.StartsWith('~') && !versionRange.StartsWith(">=", StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }

        var indexUrl = new Uri(registryUri, $"packages/{packageName}/index.json");
        var indexJson = await DownloadStringAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(indexJson);
        if (!document.RootElement.TryGetProperty("versions", out var versionsElement) || versionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new VelaRegistryRestoreException($"Registry index at '{indexUrl}' is missing a versions array.");
        }

        var versions = versionsElement.EnumerateArray()
            .Select(element => element.GetString())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(text => SemVer.TryParse(text!, out var parsed) ? parsed : null)
            .Where(static version => version is not null)
            .Cast<SemVer>()
            .Where(version => SemVer.Satisfies(version, versionRange))
            .OrderDescending()
            .ToList();

        if (versions.Count == 0)
        {
            throw new VelaRegistryRestoreException($"No published version of '{packageName}' satisfies '{versionRange}'.");
        }

        return versions[0];
    }

    private async Task<string> DownloadStringAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VelaRegistryRestoreException($"Registry request to '{url}' failed with HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VelaRegistryRestoreException($"Registry download from '{url}' failed with HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(destinationPath);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyArchiveIfTufPresent(
        string registryRoot,
        string packageName,
        string version,
        string archivePath)
    {
        var rootPath = Path.Combine(registryRoot, "tuf", "root.json");
        var targetsPath = Path.Combine(registryRoot, "tuf", "targets.json");
        if (!File.Exists(rootPath) || !File.Exists(targetsPath))
        {
            return;
        }

        try
        {
            var targets = TufVerifier.VerifyTargets(
                File.ReadAllText(rootPath),
                File.ReadAllText(targetsPath));
            VerifyArchiveAgainstTargets(targets, packageName, version, archivePath);
        }
        catch (TufVerificationException ex)
        {
            throw new VelaRegistryRestoreException(ex.Message);
        }
    }

    private async Task VerifyArchiveIfTufPresentAsync(
        Uri registryUri,
        string packageName,
        string version,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var rootUrl = new Uri(registryUri, "tuf/root.json");
        var targetsUrl = new Uri(registryUri, "tuf/targets.json");
        string rootJson;
        string targetsJson;
        try
        {
            rootJson = await DownloadStringAsync(rootUrl, cancellationToken).ConfigureAwait(false);
            targetsJson = await DownloadStringAsync(targetsUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (VelaRegistryRestoreException)
        {
            return;
        }

        try
        {
            var targets = TufVerifier.VerifyTargets(rootJson, targetsJson);
            VerifyArchiveAgainstTargets(targets, packageName, version, archivePath);
        }
        catch (TufVerificationException ex)
        {
            throw new VelaRegistryRestoreException(ex.Message);
        }
    }

    private static void VerifyArchiveAgainstTargets(
        IReadOnlyDictionary<string, TufTargetFile> targets,
        string packageName,
        string version,
        string archivePath)
    {
        var targetPath = TufVerifier.BuildArchiveTargetPath(packageName, version);
        if (!targets.TryGetValue(targetPath, out var target))
        {
            throw new VelaRegistryRestoreException($"TUF targets metadata does not list '{targetPath}'.");
        }

        TufVerifier.VerifyArchive(archivePath, target);
    }
}
