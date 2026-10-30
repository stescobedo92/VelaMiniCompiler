using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vela.Registry.Server;
using Xunit;

namespace Vela.Registry.Server.Tests;

public sealed class RegistrySecurityTests
{
    private const string TestKey = "test-only-publisher-key-0123456789abcdef";

    [Fact]
    public async Task MissingConfigurationLeavesRegistryReadOnly()
    {
        await using var registry = await RegistryFixture.CreateAsync(configureKey: false);
        using var response = await registry.PublishAsync(CreateArchive());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var index = await registry.Client.GetAsync("packages/acme.demo/index.json");
        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-test-key-0123456789abcdef")]
    public async Task PublicationRejectsMissingOrIncorrectKey(string? key)
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var response = await registry.PublishAsync(CreateArchive(), key: key);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Fact]
    public async Task PublicationRequiresHttpsByDefault()
    {
        await using var registry = await RegistryFixture.CreateAsync(allowHttp: false);
        using var response = await registry.PublishAsync(CreateArchive());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("X-Vela-ApiKey")]
    [InlineData("Authorization")]
    public async Task AuthenticatedPublicationIsPubliclyReadableAndImmutable(string header)
    {
        await using var registry = await RegistryFixture.CreateAsync();
        var bytes = CreateArchive();
        using var first = await registry.PublishAsync(bytes, header: header);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var manifestResponse = await registry.Client.GetAsync("packages/acme.demo/1.0.0/manifest.json");
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        Assert.Equal("acme.demo", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("sourceLibrary", manifest.RootElement.GetProperty("kind").GetString(), ignoreCase: true);
        Assert.Equal(bytes, await registry.Client.GetByteArrayAsync("packages/acme.demo/1.0.0/package.vlpkg"));
        using var second = await registry.PublishAsync(CreateArchive(source: "replacement"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(bytes, await registry.Client.GetByteArrayAsync("packages/acme.demo/1.0.0/package.vlpkg"));
        using var index = JsonDocument.Parse(await registry.Client.GetStringAsync("packages/acme.demo/index.json"));
        Assert.Equal("1.0.0", Assert.Single(index.RootElement.GetProperty("versions").EnumerateArray()).GetString());
        Assert.Empty(Directory.GetDirectories(Path.Combine(registry.Root, ".uploads")));
    }

    [Fact]
    public async Task MultipleCredentialsAreRejected()
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "packages/acme.demo/1.0.0") { Content = new ByteArrayContent(CreateArchive()) };
        request.Headers.Add("X-Api-Key", TestKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestKey);
        using var response = await registry.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("acme..demo", "1.0.0")]
    [InlineData("con", "1.0.0")]
    [InlineData("acme.demo.", "1.0.0")]
    [InlineData("acme.demo", "01.0.0")]
    [InlineData("acme.demo", "99999999999999.0.0")]
    [InlineData("acme.demo", "..")]
    [InlineData("acme%5Cdemo", "1.0.0")]
    public async Task RouteIdentityIsValidatedBeforeStorageAccess(string name, string version)
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var response = await registry.PublishAsync(CreateArchive(), path: $"packages/{name}/{version}");
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, response.StatusCode.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(registry.Root));
    }

    [Theory]
    [InlineData("different.name", "1.0.0")]
    [InlineData("acme.demo", "2.0.0")]
    public async Task ArchiveIdentityMustMatchRoute(string name, string version)
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var response = await registry.PublishAsync(CreateArchive(name, version));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var package = await registry.Client.GetAsync("packages/acme.demo/1.0.0/package.vlpkg");
        Assert.Equal(HttpStatusCode.NotFound, package.StatusCode);
        Assert.Empty(Directory.GetDirectories(Path.Combine(registry.Root, ".uploads")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    public async Task InvalidArchivePathsNeverBecomeVisible(string entry)
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var response = await registry.PublishAsync(CreateArchive(extraEntry: entry));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(registry.Root, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Fact]
    public async Task OversizedKnownAndChunkedUploadsAreRejected()
    {
        await using var registry = await RegistryFixture.CreateAsync(maxPackageBytes: 64);
        using var known = await registry.PublishAsync(new byte[128]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, known.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "packages/acme.demo/1.0.0") { Content = new StreamingContent(new byte[128]) };
        request.Headers.Add("X-Api-Key", TestKey);
        using var chunked = await registry.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Fact]
    public async Task ConcurrentPublicationsHaveExactlyOneWinner()
    {
        await using var registry = await RegistryFixture.CreateAsync();
        var leftBytes = CreateArchive(source: "left");
        var rightBytes = CreateArchive(source: "right");
        var replies = await Task.WhenAll(registry.PublishAsync(leftBytes), registry.PublishAsync(rightBytes));
        try
        {
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Created);
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
            var published = await registry.Client.GetByteArrayAsync("packages/acme.demo/1.0.0/package.vlpkg");
            Assert.Equal(replies[0].StatusCode == HttpStatusCode.Created ? leftBytes : rightBytes, published);
        }
        finally
        {
            foreach (var reply in replies)
            {
                reply.Dispose();
            }
        }
    }

    [Fact]
    public async Task ExpandedArchiveLimitRejectsHighlyCompressedContent()
    {
        await using var registry = await RegistryFixture.CreateAsync(maxExpandedBytes: 256);
        using var response = await registry.PublishAsync(CreateArchive(source: new string('x', 4096)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(registry.Root, ".uploads")));
    }

    [Fact]
    public async Task EntryLimitRejectsArchiveBeforePublication()
    {
        await using var registry = await RegistryFixture.CreateAsync(maxArchiveEntries: 1);
        using var response = await registry.PublishAsync(CreateArchive());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Fact]
    public async Task MissingManifestIsRejectedBeforePublication()
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "src/lib.vela", "fn value() -> Int { return 7; }");
        }

        using var response = await registry.PublishAsync(bytes.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
    }

    [Fact]
    public async Task TruncatedBodyLeavesNoPublishedVersion()
    {
        await using var registry = await RegistryFixture.CreateAsync();
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, registry.Client.BaseAddress!.Port);
        await using var stream = connection.GetStream();
        var headers = $"POST /packages/acme.demo/1.0.0 HTTP/1.1\r\nHost: localhost\r\nX-Api-Key: {TestKey}\r\nContent-Length: 1000\r\nConnection: close\r\n\r\npartial";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        var uploads = Path.Combine(registry.Root, ".uploads");
        await WaitUntilAsync(() => Directory.Exists(uploads) && Directory.GetDirectories(uploads).Length == 1);
        connection.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream);
        try
        {
            var reply = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reply.Length == 0 || reply.Contains("400 Bad Request", StringComparison.Ordinal));
        }
        catch (IOException exception) when (exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Kestrel can reset a truncated request instead of sending a reply,
            // including on macOS. Cleanup and non-publication remain required.
        }

        await WaitUntilAsync(() => Directory.GetDirectories(uploads).Length == 0);
        Assert.False(Directory.Exists(Path.Combine(registry.Root, "acme.demo")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(registry.Root, ".uploads")));
    }

    [Fact]
    public async Task ConcurrentUploadLimitRejectsAdditionalPublication()
    {
        await using var registry = await RegistryFixture.CreateAsync(maxConcurrentUploads: 1);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, registry.Client.BaseAddress!.Port);
        await using var stream = connection.GetStream();
        var headers = $"POST /packages/acme.demo/1.0.0 HTTP/1.1\r\nHost: localhost\r\nX-Api-Key: {TestKey}\r\nContent-Length: 1000\r\nConnection: close\r\n\r\npartial";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        var uploads = Path.Combine(registry.Root, ".uploads");
        await WaitUntilAsync(() => Directory.Exists(uploads) && Directory.GetDirectories(uploads).Length == 1);
        using var rejected = await registry.PublishAsync(CreateArchive());
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        connection.Client.Shutdown(SocketShutdown.Send);
        await WaitUntilAsync(() => Directory.GetDirectories(uploads).Length == 0);
    }

    [Fact]
    public async Task VersionedRootsUseOnlyCanonicalPositiveIntegers()
    {
        await using var registry = await RegistryFixture.CreateAsync();
        var directory = Path.Combine(registry.Root, "tuf");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "2.root.json"), "{\"version\":2}");
        Assert.Equal("{\"version\":2}", await registry.Client.GetStringAsync("tuf/2.root.json"));
        using var invalid = await registry.Client.GetAsync("tuf/02.root.json");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var traversal = await registry.Client.GetAsync("tuf/..%5Cmanifest.root.json");
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
    }

    private static byte[] CreateArchive(string name = "acme.demo", string version = "1.0.0", string source = "fn value() -> Int { return 7; }", string? extraEntry = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "vela.toml", $"[package]\nname = \"{name}\"\nversion = \"{version}\"\nkind = \"source-library\"\n");
            Write(zip, "src/lib.vela", source);
            if (extraEntry is not null)
            {
                Write(zip, extraEntry, "invalid");
            }
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class RegistryFixture(WebApplication app, string root, HttpClient client) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public HttpClient Client { get; } = client;

        public static async Task<RegistryFixture> CreateAsync(bool configureKey = true, bool allowHttp = true, long maxPackageBytes = 1024 * 1024,
            long maxExpandedBytes = 256 * 1024 * 1024, int maxArchiveEntries = 10000, int maxConcurrentUploads = 4)
        {
            var root = Path.Combine(Path.GetTempPath(), "vela-registry-tests", Guid.NewGuid().ToString("N"));
            var app = RegistryApplication.Build([], builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RegistryRoot"] = root,
                    ["Registry:ApiKey"] = configureKey ? TestKey : null,
                    ["Registry:AllowInsecureHttp"] = allowHttp.ToString(),
                    ["Registry:MaxPackageBytes"] = maxPackageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Registry:MaxExpandedBytes"] = maxExpandedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Registry:MaxArchiveEntries"] = maxArchiveEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Registry:MaxConcurrentUploads"] = maxConcurrentUploads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new RegistryFixture(app, root, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) });
        }

        public async Task<HttpResponseMessage> PublishAsync(byte[] bytes, string? key = TestKey, string header = "X-Api-Key", string path = "packages/acme.demo/1.0.0")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
            if (key is not null)
            {
                request.Headers.Add(header, header == "Authorization" ? "Bearer " + key : key);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vela-registry-tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(Root).StartsWith(expectedRoot, StringComparison.Ordinal) && Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
