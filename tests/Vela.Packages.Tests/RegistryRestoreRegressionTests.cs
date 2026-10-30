using System.Net;
using System.Text;
using System.Text.Json;
using Vela.Packages;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class RegistryRestoreRegressionTests : IDisposable
{
    private const string PackageName = "acme.demo";
    private const string Version = "1.0.0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-packages-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileRegistry_IsolatesSourcesAndIgnoresUnscopedCache()
    {
        var firstRoot = Path.Combine(_root, "first-registry");
        var secondRoot = Path.Combine(_root, "second-registry");
        WritePackage(Path.Combine(firstRoot, PackageName, Version), "first registry");
        WritePackage(Path.Combine(secondRoot, PackageName, Version), "second registry");
        var legacyPackage = WritePackage(Path.Combine(_root, "legacy-source"), "unscoped cache");
        var cache = new VelaPackageCache(Path.Combine(_root, "cache"));
        cache.StoreExtracted(PackageName, Version, legacyPackage);

        using var client = new VelaRestoreClient(cache, options: new VelaRestoreOptions { AllowUnsigned = true });
        var first = await client.RestoreAsync(PackageName, Version, new Uri(firstRoot).AbsoluteUri);
        var second = await client.RestoreAsync(PackageName, Version, new Uri(secondRoot).AbsoluteUri);
        var firstAgain = await client.RestoreAsync(PackageName, Version, new Uri(firstRoot).AbsoluteUri);

        Assert.Equal("first registry", await ReadContentAsync(first));
        Assert.Equal("second registry", await ReadContentAsync(second));
        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(cache.GetPackagePath(PackageName, Version), first.Path);
        Assert.Equal(first.Path, firstAgain.Path);
    }

    [Theory]
    [InlineData("https://first.invalid/", "https://second.invalid/")]
    [InlineData("https://registry.invalid/first/", "https://registry.invalid/second/")]
    public async Task HttpRegistry_IsolatesSourcesAndArchives(string firstUrl, string secondUrl)
    {
        var firstArchive = CreateArchive("first", "first registry");
        var secondArchive = CreateArchive("second", "second registry");
        var cache = new VelaPackageCache(Path.Combine(_root, "cache"));
        var firstSource = new Uri(firstUrl);
        var secondSource = new Uri(secondUrl);
        using var handler = new StubHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            return uri.AbsolutePath.EndsWith("package.vlpkg", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(uri.AbsoluteUri.StartsWith(firstSource.AbsoluteUri, StringComparison.Ordinal) ? firstArchive : secondArchive)
                }
                : MetadataResponse(uri.AbsolutePath);
        });
        using var client = new VelaRestoreClient(cache, handler, options: new VelaRestoreOptions { AllowUnsigned = true });

        var first = await client.RestoreAsync(PackageName, Version, firstSource.AbsoluteUri);
        var second = await client.RestoreAsync(PackageName, Version, secondSource.AbsoluteUri);

        Assert.Equal("first registry", await ReadContentAsync(first));
        Assert.Equal("second registry", await ReadContentAsync(second));
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(firstArchive, await File.ReadAllBytesAsync(cache.ForRegistry(firstSource).GetArchivePath(PackageName, Version)));
        Assert.Equal(secondArchive, await File.ReadAllBytesAsync(cache.ForRegistry(secondSource).GetArchivePath(PackageName, Version)));
    }

    [Theory]
    [InlineData(500, 200)]
    [InlineData(200, 500)]
    [InlineData(404, 500)]
    [InlineData(401, 200)]
    [InlineData(200, 403)]
    [InlineData(200, 429)]
    [InlineData(404, 200)]
    [InlineData(200, 404)]
    public async Task HttpRegistry_RejectsUnavailableOrIncompleteTufMetadata(int rootStatus, int targetsStatus)
    {
        var archive = CreateArchive("source", "package content");
        var source = new Uri("https://registry.invalid/");
        var cache = new VelaPackageCache(Path.Combine(_root, "cache"));
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/tuf/root.json" => new HttpResponseMessage((HttpStatusCode)rootStatus) { Content = new StringContent("{}") },
            "/tuf/targets.json" => new HttpResponseMessage((HttpStatusCode)targetsStatus) { Content = new StringContent("{}") },
            var path when path.EndsWith("package.vlpkg", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            },
            var path => MetadataResponse(path)
        });
        using var client = new VelaRestoreClient(cache, handler, options: new VelaRestoreOptions { AllowUnsigned = true });

        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() =>
            client.RestoreAsync(PackageName, Version, source.AbsoluteUri));

        Assert.Contains("TUF metadata", error.Message, StringComparison.Ordinal);
        Assert.False(cache.ForRegistry(source).HasExtracted(PackageName, Version));
        Assert.False(File.Exists(cache.ForRegistry(source).GetArchivePath(PackageName, Version)));
    }

    [Theory]
    [InlineData("root.json")]
    [InlineData("targets.json")]
    public async Task FileRegistry_RejectsIncompleteTufMetadata(string presentFile)
    {
        var archive = CreateArchive("source", "package content");
        var registryRoot = Path.Combine(_root, "registry");
        var versionDirectory = Path.Combine(registryRoot, PackageName, Version);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "package.vlpkg"), archive);
        Directory.CreateDirectory(Path.Combine(registryRoot, "tuf"));
        await File.WriteAllTextAsync(Path.Combine(registryRoot, "tuf", presentFile), "{}");
        using var client = new VelaRestoreClient(new VelaPackageCache(Path.Combine(_root, "cache")), options: new VelaRestoreOptions { AllowUnsigned = true });

        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() =>
            client.RestoreAsync(PackageName, Version, new Uri(registryRoot).AbsoluteUri));

        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpRegistry_ReportsMalformedTufAsRestoreError()
    {
        var archive = CreateArchive("source", "package content");
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/tuf/root.json" or "/tuf/targets.json" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"signed\":null,\"signatures\":[]}")
            },
            var path when path.EndsWith("package.vlpkg", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            },
            var path => MetadataResponse(path)
        });
        using var client = new VelaRestoreClient(new VelaPackageCache(Path.Combine(_root, "cache")), handler, options: new VelaRestoreOptions { AllowUnsigned = true });

        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() =>
            client.RestoreAsync(PackageName, Version, "https://registry.invalid/"));

        Assert.Contains("TUF metadata", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("^1.0.0", "1.9.0")]
    [InlineData("~1.0.0", "1.0.8")]
    [InlineData(">=1.0.0 <2.0.0", "1.9.0")]
    [InlineData("1.0.0 - 1.0.8", "1.0.8")]
    [InlineData("1.0.0-beta || 1.9.0", "1.9.0")]
    [InlineData("1.0.0+build || 1.9.0", "1.9.0")]
    [InlineData("*", "2.0.0")]
    [InlineData("latest", "2.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    public async Task HttpRegistry_ResolvesVersionsConsistentlyWithFileRegistry(string range, string expected)
    {
        var versions = new[] { "1.0.0", "1.0.8", "1.9.0", "2.0.0" };
        var archives = versions.ToDictionary(version => version, version => CreateArchive("source-" + version, version, version));
        var registryRoot = Path.Combine(_root, "registry");
        foreach (var version in versions)
        {
            WritePackage(Path.Combine(registryRoot, PackageName, version), version, version);
        }

        var indexRequested = false;
        using var handler = new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("index.json", StringComparison.Ordinal))
            {
                indexRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { versions }), Encoding.UTF8, "application/json")
                };
            }

            if (path.StartsWith("/tuf/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var requestedVersion = path.Split('/')[3];
            return path.EndsWith("package.vlpkg", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archives[requestedVersion]) }
                : MetadataResponse(path, requestedVersion);
        });
        using var client = new VelaRestoreClient(new VelaPackageCache(Path.Combine(_root, "cache")), handler, options: new VelaRestoreOptions { AllowUnsigned = true });

        var http = await client.RestoreAsync(PackageName, range, "https://registry.invalid/");
        var file = await client.RestoreAsync(PackageName, range, new Uri(registryRoot).AbsoluteUri);

        Assert.Equal(expected, http.Version);
        Assert.Equal(file.Version, http.Version);
        Assert.Equal(expected, await ReadContentAsync(http));
        Assert.Equal(range != Version, indexRequested);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Task<string> ReadContentAsync(VelaRestoredPackage package) =>
        File.ReadAllTextAsync(Path.Combine(package.Path, "src", "lib.vela"));

    private static string WritePackage(string directory, string content, string version = Version)
    {
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "vela.toml"), $"[package]\nname = \"{PackageName}\"\nversion = \"{version}\"\nkind = \"source-library\"\n");
        File.WriteAllText(Path.Combine(directory, "src", "lib.vela"), content);
        return directory;
    }

    private byte[] CreateArchive(string fixtureName, string content, string version = Version)
    {
        var directory = WritePackage(Path.Combine(_root, fixtureName), content, version);
        var archive = Path.Combine(_root, fixtureName + VelaDependencyArchive.Extension);
        VelaDependencyArchive.Pack(directory, archive);
        return File.ReadAllBytes(archive);
    }

    private static HttpResponseMessage MetadataResponse(string path, string version = Version)
    {
        if (!path.EndsWith("manifest.json", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var manifest = new VelaPackageManifest(PackageName, version, VelaPackageKind.SourceLibrary, new Dictionary<string, string>());
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(manifest, VelaPackageJsonContext.Default.VelaPackageManifest), Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
