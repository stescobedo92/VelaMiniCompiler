using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vela.Packages.Tuf;

namespace Vela.Packages;

/// <summary>Thrown when registry restore fails.</summary>
public sealed class VelaRegistryRestoreException(string message) : Exception(message);

/// <summary>Result of restoring one package from a registry.</summary>
public sealed record VelaRestoredPackage(string Name, string Version, string Path, VelaPackageManifest Manifest);

/// <summary>Restores packages from file and HTTP registries with explicit, persistent trust.</summary>
public sealed class VelaRestoreClient : IDisposable
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const long MaximumArchiveBytes = 64 * 1024 * 1024;
    private const int MaximumRootRotations = 32;
    private readonly VelaPackageCache _cache;
    private readonly HttpClient _http;
    private readonly VelaRestoreOptions _options;

    /// <summary>Initializes a client with the default cache and signed-registry policy.</summary>
    public VelaRestoreClient() : this(new VelaPackageCache()) { }

    /// <summary>Initializes a client with an explicit cache, optional HTTP handler and trust policy.</summary>
    public VelaRestoreClient(VelaPackageCache cache, HttpMessageHandler? handler = null, VelaRestoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _options = options ?? new VelaRestoreOptions();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Gets the package cache used by this client.</summary>
    public VelaPackageCache Cache => _cache;

    /// <summary>Restores a package using a pinned root, persisted trust, or an explicit unsigned opt-in.</summary>
    /// <remarks>Once a registry is trusted, unsigned fallback is never permitted. Cached contents are
    /// reconstructed from a freshly verified archive on every restore. This implements the root/targets
    /// subset of TUF, not timestamp, snapshot or delegated roles.</remarks>
    public async Task<VelaRestoredPackage> RestoreAsync(string packageName, string versionRange, string registryUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = timeout.Token;
        if (!IsValidPackageName(packageName))
        {
            throw new VelaRegistryRestoreException("Package names must contain only ASCII letters, digits, '.', '_' and '-', starting with a letter or digit.");
        }
        if (!Uri.TryCreate(registryUrl, UriKind.Absolute, out var registryUri)
            || registryUri.Scheme is not ("file" or "http" or "https")
            || !string.IsNullOrEmpty(registryUri.Query) || !string.IsNullOrEmpty(registryUri.Fragment))
        {
            throw new VelaRegistryRestoreException("Registry URL must be an absolute file, HTTP or HTTPS URI without query or fragment.");
        }

        var cache = _cache.ForRegistry(registryUri);
        try
        {
            using var trustStore = await TufTrustStore.OpenAsync(cache.RootDirectory, cancellationToken).ConfigureAwait(false);
            var targets = await UpdateTrustAsync(registryUri, trustStore, cancellationToken).ConfigureAwait(false);
            var version = targets is not null ? ResolveTrustedVersion(targets, packageName, versionRange)
                : registryUri.IsFile ? ResolveFileRegistryVersion(Path.Combine(registryUri.LocalPath, packageName), versionRange)
                : await ResolveHttpRegistryVersionAsync(packageName, versionRange, registryUri, cancellationToken).ConfigureAwait(false);
            return await RestoreArchiveAsync(packageName, version.ToString(), registryUri, cache, targets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TufVerificationException or JsonException or IOException
            or VelaDependencyArchiveException or FormatException or OverflowException or DecoderFallbackException)
        {
            throw new VelaRegistryRestoreException(ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static bool IsValidPackageName(string name) => name.Length is > 0 and <= 128
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    private async Task<IReadOnlyDictionary<string, TufTargetFile>?> UpdateTrustAsync(Uri registryUri,
        TufTrustStore store, CancellationToken cancellationToken)
    {
        var updateTime = DateTimeOffset.UtcNow;
        var state = store.Load();
        if (_options.TrustedRootPath is not null)
        {
            var initialRoot = await ReadLocalMetadataAsync(Path.GetFullPath(_options.TrustedRootPath), cancellationToken).ConfigureAwait(false)
                ?? throw new VelaRegistryRestoreException("The trusted root file does not exist.");
            _ = TufVerifier.VerifyRoot(initialRoot, updateTime, allowExpired: true);
            var initialDigest = TufVerifier.SignedDigest(initialRoot);
            if (state is null)
            {
                state = new TufTrustState(initialRoot, initialDigest, 0, null);
                store.Save(state);
            }
            else if (state.BootstrapDigest != initialDigest && TufVerifier.SignedDigest(state.RootJson) != initialDigest)
            {
                throw new TufVerificationException("The supplied initial root does not match this registry's persisted trust. Trust cannot be reset during restore.");
            }
        }

        if (state is null)
        {
            // Inspect both endpoints even in unsigned mode: partial metadata and HTTP failures
            // cannot silently downgrade a signed registry into an unsigned one.
            var remoteRoot = await ReadRegistryMetadataAsync(registryUri, "root.json", cancellationToken).ConfigureAwait(false);
            var remoteTargets = await ReadRegistryMetadataAsync(registryUri, "targets.json", cancellationToken).ConfigureAwait(false);
            if ((remoteRoot is null) != (remoteTargets is null))
            {
                throw new TufVerificationException("TUF metadata is incomplete: both root.json and targets.json are required.");
            }
            if (remoteRoot is not null || !_options.AllowUnsigned)
            {
                throw new TufVerificationException("TUF metadata requires a trusted root obtained out of band. Supply --trusted-root, or use --allow-unsigned only for a registry without TUF metadata.");
            }
            return null;
        }

        var currentRoot = TufVerifier.VerifyRoot(state.RootJson, updateTime, allowExpired: true);
        var rotations = 0;
        while (currentRoot.SignedPayload.Version < int.MaxValue)
        {
            var nextVersion = currentRoot.SignedPayload.Version + 1;
            var nextRootJson = await ReadRegistryMetadataAsync(registryUri, $"{nextVersion}.root.json", cancellationToken).ConfigureAwait(false);
            if (nextRootJson is null)
            {
                break;
            }
            if (rotations++ >= MaximumRootRotations)
            {
                throw new TufVerificationException("TUF root rotation limit exceeded; retry to continue the verified chain.");
            }
            TufVerifier.VerifyRootRotation(state.RootJson, nextRootJson, updateTime);
            state = state with { RootJson = nextRootJson };
            store.Save(state);
            currentRoot = TufVerifier.VerifyRoot(nextRootJson, updateTime, allowExpired: true);
        }

        // The alias must describe the same final root; stale or unverified replacement
        // roots cannot bypass the sequential, dual-signed rotation chain.
        var rootAlias = await ReadRegistryMetadataAsync(registryUri, "root.json", cancellationToken).ConfigureAwait(false)
            ?? throw new TufVerificationException("TUF root metadata is required for this trusted registry.");
        var advertisedRoot = TufVerifier.VerifyRoot(rootAlias, updateTime, allowExpired: true);
        if (advertisedRoot.SignedPayload.Version != currentRoot.SignedPayload.Version
            || TufVerifier.SignedDigest(rootAlias) != TufVerifier.SignedDigest(state.RootJson))
        {
            throw new TufVerificationException("TUF root rollback or unverified root replacement detected; publish the complete numbered root chain.");
        }
        _ = TufVerifier.VerifyRoot(state.RootJson, updateTime);
        var targetsJson = await ReadRegistryMetadataAsync(registryUri, "targets.json", cancellationToken).ConfigureAwait(false)
            ?? throw new TufVerificationException("TUF targets metadata is required for this trusted registry.");
        var targets = TufVerifier.VerifyTargetsMetadata(state.RootJson, targetsJson, updateTime);
        var digest = TufVerifier.SignedDigest(targetsJson);
        if (targets.SignedPayload.Version < state.TargetsVersion
            || (targets.SignedPayload.Version == state.TargetsVersion && state.TargetsDigest != digest))
        {
            throw new TufVerificationException("TUF targets rollback or same-version metadata replacement detected.");
        }
        store.Save(state with { TargetsVersion = targets.SignedPayload.Version, TargetsDigest = digest });
        return targets.SignedPayload.Targets;
    }

    private static SemVer ResolveTrustedVersion(IReadOnlyDictionary<string, TufTargetFile> targets,
        string packageName, string versionRange)
    {
        var prefix = packageName + "/";
        var suffix = "/package" + VelaDependencyArchive.Extension;
        var versions = targets.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)
            && key.EndsWith(suffix, StringComparison.Ordinal))
            .Select(key => key[prefix.Length..^suffix.Length])
            .Where(static version => SemVer.TryParse(version, out var parsed) && parsed!.ToString() == version)
            .Select(SemVer.Parse).Where(version => SemVer.Satisfies(version, versionRange)).OrderDescending().ToList();
        return versions.Count > 0 ? versions[0]
            : throw new VelaRegistryRestoreException($"No signed target for '{packageName}' satisfies '{versionRange}'.");
    }

    private async Task<VelaRestoredPackage> RestoreArchiveAsync(string name, string version, Uri registryUri,
        VelaPackageCache cache, IReadOnlyDictionary<string, TufTargetFile>? targets, CancellationToken cancellationToken)
    {
        TufTargetFile? target = null;
        if (targets is not null && !targets.TryGetValue(TufVerifier.BuildArchiveTargetPath(name, version), out target))
        {
            throw new TufVerificationException("The package archive is missing from signed targets metadata.");
        }
        var temporary = Path.Combine(Path.GetTempPath(), "vela-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var archivePath = Path.Combine(temporary, "package.vlpkg");
        try
        {
            var cachedArchive = cache.GetArchivePath(name, version);
            var useCachedArchive = false;
            if (target is not null && File.Exists(cachedArchive))
            {
                try
                {
                    await CopyLocalArchiveAsync(cachedArchive, archivePath, cancellationToken).ConfigureAwait(false);
                    TufVerifier.VerifyArchive(archivePath, target);
                    useCachedArchive = true;
                }
                catch (Exception ex) when (ex is TufVerificationException or VelaRegistryRestoreException or IOException)
                {
                    File.Delete(archivePath);
                }
            }
            if (!useCachedArchive)
            {
                if (registryUri.IsFile)
                {
                    var source = Path.Combine(registryUri.LocalPath, name, version);
                    var sourceArchive = Path.Combine(source, "package.vlpkg");
                    if (File.Exists(sourceArchive))
                    {
                        await CopyLocalArchiveAsync(sourceArchive, archivePath, cancellationToken).ConfigureAwait(false);
                    }
                    else if (target is null && Directory.Exists(source))
                    {
                        VelaDependencyArchive.Pack(source, archivePath);
                        if (new FileInfo(archivePath).Length > MaximumArchiveBytes)
                        {
                            throw new VelaRegistryRestoreException("Package archive exceeds its size limit.");
                        }
                    }
                    else
                    {
                        throw new VelaRegistryRestoreException("The registry package archive does not exist.");
                    }
                }
                else
                {
                    await DownloadFileAsync(new Uri(registryUri, $"packages/{name}/{version}/package.vlpkg"), archivePath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (target is not null)
            {
                TufVerifier.VerifyArchive(archivePath, target);
            }
            var extracted = VelaDependencyArchive.Unpack(archivePath, Path.Combine(temporary, "extracted"));
            var manifest = VelaPackageManifest.FromToml(Path.Combine(extracted, "vela.toml"));
            if (manifest.Name != name || manifest.Version != version)
            {
                throw new VelaRegistryRestoreException("The archive manifest identity does not match the requested package and version.");
            }
            var cached = cache.StoreExtracted(name, version, extracted);
            cache.StoreArchive(name, version, archivePath);
            return new VelaRestoredPackage(name, version, cached, manifest);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private async Task<string?> ReadRegistryMetadataAsync(Uri registryUri, string filename, CancellationToken cancellationToken)
        => registryUri.IsFile
            ? await ReadLocalMetadataAsync(Path.Combine(registryUri.LocalPath, "tuf", filename), cancellationToken).ConfigureAwait(false)
            : await DownloadOptionalTufMetadataAsync(new Uri(registryUri, "tuf/" + filename), cancellationToken).ConfigureAwait(false);

    private static async Task<string?> ReadLocalMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) { return null; }
        await using var stream = File.OpenRead(path);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static async Task CopyLocalArchiveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await CopyLimitedAsync(input, output, MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
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
        var normalizedRange = versionRange.Trim();
        if (!normalizedRange.Any(char.IsWhiteSpace)
            && !normalizedRange.Contains("||", StringComparison.Ordinal)
            && SemVer.TryParse(normalizedRange, out var exact))
        {
            var manifestUrl = new Uri(registryUri, $"packages/{packageName}/{exact!.ToString()}/manifest.json");
            _ = await DownloadStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            return exact;
        }

        var indexUrl = new Uri(registryUri, $"packages/{packageName}/index.json");
        var indexJson = await DownloadStringAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(indexJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("versions", out var versionsElement) || versionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new VelaRegistryRestoreException($"Registry index at '{indexUrl}' is missing a versions array.");
        }

        if (versionsElement.EnumerateArray().Any(static element => element.ValueKind != JsonValueKind.String))
        {
            throw new VelaRegistryRestoreException("Registry versions must be JSON strings.");
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
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VelaRegistryRestoreException($"Registry request to '{url}' failed with HTTP {(int)response.StatusCode}.");
        }
        return await ReadResponseMetadataAsync(response, cancellationToken).ConfigureAwait(false);
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
        await CopyLimitedAsync(stream, file, MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DownloadOptionalTufMetadataAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        if (!response.IsSuccessStatusCode)
        {
            throw new VelaRegistryRestoreException($"TUF metadata request to '{url}' failed with HTTP {(int)response.StatusCode}.");
        }
        return await ReadResponseMetadataAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadResponseMetadataAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new VelaRegistryRestoreException("Registry response exceeds its configured size limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
