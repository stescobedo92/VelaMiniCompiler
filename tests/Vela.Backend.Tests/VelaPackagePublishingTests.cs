using System.IO.Compression;
using System.Net;
using System.Text;
using Vela.Backend;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaPackagePublishingTests
{
    [Fact]
    public void PackCreatesArchiveWithManifestSourcesAndMetadata()
    {
        var root = CreatePackage("vela.demo", "0.1.0", "source-library");
        try
        {
            var archivePath = VelaPackageArchive.Pack(root);

            Assert.True(File.Exists(archivePath));
            Assert.Equal("vela.demo.0.1.0.vpkg", Path.GetFileName(archivePath));

            var manifest = VelaPackageArchive.ReadManifest(archivePath);
            Assert.Equal(VelaPackageArchive.FormatVersion, manifest.FormatVersion);
            Assert.Equal("vela.demo", manifest.Name);
            Assert.Equal("0.1.0", manifest.Version);
            Assert.Equal("source-library", manifest.Kind);
            Assert.Contains(manifest.Files, static file => file.Path == "vela.toml");
            Assert.Contains(manifest.Files, static file => file.Path == "src/lib.vela");
            Assert.All(manifest.Files, static file => Assert.Equal(64, file.Sha256.Length));

            using var archive = ZipFile.OpenRead(archivePath);
            Assert.NotNull(archive.GetEntry("vpkg.json"));
            Assert.NotNull(archive.GetEntry("src/lib.vela"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackAcceptsVersionOverrideAndRejectsInvalidSemVer()
    {
        var root = CreatePackage("vela.demo", "0.1.0", "source-library");
        try
        {
            var archivePath = VelaPackageArchive.Pack(root, "1.2.3-beta.1");
            Assert.Equal("vela.demo.1.2.3-beta.1.vpkg", Path.GetFileName(archivePath));
            Assert.Equal("1.2.3-beta.1", VelaPackageArchive.ReadManifest(archivePath).Version);

            var failure = Assert.Throws<VelaRegistryException>(() => VelaPackageArchive.Pack(root, "not-a-version"));
            Assert.Contains("SemVer", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadManifestRejectsNonPackageArchives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vela-not-a-package-{Guid.NewGuid():N}.vpkg");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("unrelated.txt");
            }

            var failure = Assert.Throws<VelaRegistryException>(() => VelaPackageArchive.ReadManifest(path));
            Assert.Contains("vpkg.json", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CredentialStoreSavesFindsAndRemovesPerNormalizedSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vela-credentials-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VelaRegistryCredentialStore(path);
            Assert.Null(store.Find("https://packages.vela.dev/v3/index.json"));

            store.Save("https://packages.vela.dev/v3/index.json/", "first-key");
            store.Save("https://other.example/v3/index.json", "other-key");
            Assert.Equal("first-key", store.Find("https://packages.vela.dev/v3/index.json"));
            Assert.Equal("other-key", store.Find("https://other.example/v3/index.json/"));

            store.Save("https://packages.vela.dev/v3/index.json", "rotated-key");
            Assert.Equal("rotated-key", store.Find("https://packages.vela.dev/v3/index.json"));

            Assert.True(store.Remove("https://packages.vela.dev/v3/index.json"));
            Assert.False(store.Remove("https://packages.vela.dev/v3/index.json"));
            Assert.Null(store.Find("https://packages.vela.dev/v3/index.json"));
            Assert.Equal("other-key", store.Find("https://other.example/v3/index.json"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolvePublishEndpointReadsServiceIndexAndPassesThroughDirectUrls()
    {
        var handler = new StubHttpHandler(request => request.Method == HttpMethod.Get
            ? Json("""{"version":"3.0.0","resources":[{"@id":"https://registry.example/api/v3/publish","@type":"PackagePublish/3.0.0"}]}""")
            : throw new InvalidOperationException("Unexpected request."));
        using var client = new VelaRegistryClient(handler);

        Assert.Equal(
            "https://registry.example/api/v3/publish",
            await client.ResolvePublishEndpointAsync("https://registry.example/v3/index.json"));
        Assert.Equal(
            "https://registry.example/api/upload",
            await client.ResolvePublishEndpointAsync("https://registry.example/api/upload/"));
    }

    [Fact]
    public async Task ResolvePublishEndpointFailsWhenIndexHasNoPublishResource()
    {
        var handler = new StubHttpHandler(static _ => Json("""{"version":"3.0.0","resources":[]}"""));
        using var client = new VelaRegistryClient(handler);

        var failure = await Assert.ThrowsAsync<VelaRegistryException>(
            () => client.ResolvePublishEndpointAsync("https://registry.example/v3/index.json"));
        Assert.Contains("PackagePublish", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushSendsApiKeyAndArchiveAndMapsRegistryResponses()
    {
        var root = CreatePackage("vela.demo", "0.1.0", "source-library");
        try
        {
            var archivePath = VelaPackageArchive.Pack(root);
            HttpRequestMessage? observed = null;
            var handler = new StubHttpHandler(request =>
            {
                observed = request;
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            using var client = new VelaRegistryClient(handler);

            var result = await client.PushAsync(archivePath, "https://registry.example/api/v3/publish", "secret-key");

            Assert.Equal(201, result.StatusCode);
            Assert.Contains("vela.demo v0.1.0", result.Message, StringComparison.Ordinal);
            Assert.NotNull(observed);
            Assert.Equal(HttpMethod.Put, observed!.Method);
            Assert.Equal("secret-key", Assert.Single(observed.Headers.GetValues(VelaRegistryClient.ApiKeyHeader)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.Conflict, "already exists")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task PushSurfacesRegistryFailuresAsActionableErrors(HttpStatusCode status, string expectedFragment)
    {
        var root = CreatePackage("vela.demo", "0.1.0", "source-library");
        try
        {
            var archivePath = VelaPackageArchive.Pack(root);
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(status));
            using var client = new VelaRegistryClient(handler);

            var failure = await Assert.ThrowsAsync<VelaRegistryException>(
                () => client.PushAsync(archivePath, "https://registry.example/api/v3/publish", "secret-key"));
            Assert.Contains(expectedFragment, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePackage(string name, string version, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vela-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "vela.toml"), $"""
            [package]
            name = "{name}"
            version = "{version}"
            kind = "{kind}"
            """);
        File.WriteAllText(Path.Combine(root, "src", "lib.vela"), """
            public fn double(value: Int) -> Int {
                return value * 2;
            }
            """);
        File.WriteAllText(Path.Combine(root, "README.md"), $"# {name}");
        return root;
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
