using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vela.Packages.Tuf;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class TrustedRestoreTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("x/y")]
    [InlineData("x\\y")]
    [InlineData("x:y")]
    [InlineData("file.")]
    [InlineData("file ")]
    public void CacheRejectsUnsafeIdentityComponents(string value)
    {
        var cache = new VelaPackageCache(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => cache.GetPackagePath(value, "1.0.0"));
        Assert.Throws<ArgumentException>(() => cache.GetArchivePath("acme.demo", value));
    }

    [Fact]
    public void SemVerTryParseRejectsOverflowWithoutThrowing()
    {
        Assert.False(SemVer.TryParse("999999999999999999999.0.0", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedRegistryRequiresAnOutOfBandRootEvenWithUnsignedOptIn(bool allowUnsigned)
    {
        using var fixture = new RegistryFixture();
        using var client = fixture.Client(pin: false, allowUnsigned: allowUnsigned);
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(client));
        Assert.Contains("trusted root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedRegistryRequiresExplicitOptIn()
    {
        using var fixture = new RegistryFixture();
        File.Delete(fixture.MetadataPath("root.json"));
        File.Delete(fixture.MetadataPath("targets.json"));
        using var secureClient = fixture.Client(pin: false);
        await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(secureClient));
        using var explicitClient = fixture.Client(pin: false, allowUnsigned: true);
        Assert.Equal("1.0.0", (await fixture.Restore(explicitClient)).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustPersistsAndCachedExtractionIsReconstructed(bool http)
    {
        using var fixture = new RegistryFixture();
        using (var first = fixture.Client(http: http))
        {
            var restored = await fixture.Restore(first, http);
            await File.WriteAllTextAsync(Path.Combine(restored.Path, "src", "lib.vela"), "tampered");
        }
        using var second = fixture.Client(pin: false, http: http);
        var clean = await fixture.Restore(second, http);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(clean.Path, "src", "lib.vela")));
        var cache = fixture.Cache.ForRegistry(fixture.Uri(http));
        await File.WriteAllTextAsync(cache.GetArchivePath("acme.demo", "1.0.0"), "corrupted archive");
        var repaired = await fixture.Restore(second, http);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(repaired.Path, "src", "lib.vela")));
    }

    [Fact]
    public async Task OversizedCachedArchiveIsReplacedFromAuthenticatedSource()
    {
        using var fixture = new RegistryFixture();
        using var client = fixture.Client();
        await fixture.Restore(client);
        var archive = fixture.Cache.ForRegistry(fixture.Uri(false)).GetArchivePath("acme.demo", "1.0.0");
        using (var corrupt = File.OpenWrite(archive))
        {
            corrupt.SetLength(64L * 1024 * 1024 + 1);
        }

        var repaired = await fixture.Restore(client);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(repaired.Path, "src", "lib.vela")));
        Assert.True(new FileInfo(archive).Length < 64L * 1024 * 1024);
    }

    [Fact]
    public async Task PersistedTrustPreventsUnsignedFallbackAndRejectsChangedInitialRoot()
    {
        using var fixture = new RegistryFixture();
        using (var first = fixture.Client()) { await fixture.Restore(first); }
        File.Delete(fixture.MetadataPath("root.json"));
        File.Delete(fixture.MetadataPath("targets.json"));
        using var fallback = fixture.Client(pin: false, allowUnsigned: true);
        await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(fallback));
        var altered = JsonNode.Parse(fixture.InitialRoot)!;
        altered["signed"]!["version"] = 2;
        await File.WriteAllTextAsync(fixture.Anchor, Sign(altered, (TufTestSigner.TestKeyId, fixture.Key)));
        using var replacement = fixture.Client();
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(replacement));
        Assert.Contains("cannot be reset", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootRotationRequiresBothOldAndNewSignatures(bool http)
    {
        using var fixture = new RegistryFixture();
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var next = NextRoot(fixture.InitialRoot, nextKey, 2);
        var invalidRotation = Sign(next, ("next-key", nextKey));
        File.WriteAllText(fixture.MetadataPath("2.root.json"), invalidRotation);
        File.WriteAllText(fixture.MetadataPath("root.json"), invalidRotation);
        using (var client = fixture.Client(http: http))
        {
            var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(client, http));
            Assert.Contains("insufficient", error.Message, StringComparison.Ordinal);
        }
        var validRotation = Sign(next, (TufTestSigner.TestKeyId, fixture.Key), ("next-key", nextKey));
        File.WriteAllText(fixture.MetadataPath("2.root.json"), validRotation);
        File.WriteAllText(fixture.MetadataPath("root.json"), validRotation);
        fixture.WriteTargets(2, nextKey, "next-key");
        using var updated = fixture.Client(pin: false, http: http);
        Assert.Equal("1.0.0", (await fixture.Restore(updated, http)).Version);
        File.WriteAllText(fixture.MetadataPath("root.json"), fixture.InitialRoot);
        var rollback = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(updated, http));
        Assert.Contains("rollback", rollback.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootRotationRejectsSkippedVersions()
    {
        using var fixture = new RegistryFixture();
        var skipped = JsonNode.Parse(fixture.InitialRoot)!;
        skipped["signed"]!["version"] = 3;
        File.WriteAllText(fixture.MetadataPath("2.root.json"), Sign(skipped, (TufTestSigner.TestKeyId, fixture.Key)));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(client));
        Assert.Contains("exactly one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredIntermediateRootCanRotateToAFreshFinalRoot()
    {
        using var fixture = new RegistryFixture();
        var intermediate = JsonNode.Parse(fixture.InitialRoot)!;
        intermediate["signed"]!["version"] = 2;
        intermediate["signed"]!["expires"] = "2000-01-01T00:00:00Z";
        File.WriteAllText(fixture.MetadataPath("2.root.json"), Sign(intermediate, (TufTestSigner.TestKeyId, fixture.Key)));
        var final = JsonNode.Parse(fixture.InitialRoot)!;
        final["signed"]!["version"] = 3;
        var finalJson = Sign(final, (TufTestSigner.TestKeyId, fixture.Key));
        File.WriteAllText(fixture.MetadataPath("3.root.json"), finalJson);
        File.WriteAllText(fixture.MetadataPath("root.json"), finalJson);
        using var client = fixture.Client();
        Assert.Equal("1.0.0", (await fixture.Restore(client)).Version);
    }

    [Fact]
    public async Task TargetsRejectRollbackAndSameVersionReplacementAcrossClients()
    {
        using var fixture = new RegistryFixture();
        fixture.WriteTargets(2);
        using (var first = fixture.Client()) { await fixture.Restore(first); }
        using var second = fixture.Client(pin: false);
        fixture.WriteTargets(1);
        await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(second));
        fixture.WriteTargets(2, edit: signed => signed["custom"] = "changed payload");
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(second));
        Assert.Contains("same-version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredFinalMetadataIsRejectedEvenWithACachedPackage()
    {
        using var fixture = new RegistryFixture();
        using var client = fixture.Client();
        await fixture.Restore(client);
        fixture.WriteTargets(2, edit: signed => signed["expires"] = "2000-01-01T00:00:00Z");
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(client));
        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRootCannotReplacePinnedRootAtTheSameVersion()
    {
        using var fixture = new RegistryFixture();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.MetadataPath("root.json"), Sign(NextRoot(fixture.InitialRoot, attacker, 1), ("next-key", attacker)));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => fixture.Restore(client));
        Assert.Contains("replacement", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"versions\":[null,1,{}]}")]
    public async Task MalformedUnsignedIndexProducesControlledError(string index)
    {
        using var fixture = new RegistryFixture();
        using var handler = new ResponseHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/tuf/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index) });
        using var client = new VelaRestoreClient(fixture.Cache, handler, new VelaRestoreOptions { AllowUnsigned = true });
        await Assert.ThrowsAsync<VelaRegistryRestoreException>(() => client.RestoreAsync("acme.demo", "*", "https://registry.invalid/"));
    }

    [Fact]
    public void CanonicalJsonPreservesUnicodeInsteadOfTruncatingCharacters()
    {
        var bytes = TufCanonicalJson.EncodeSignedPortion(Encoding.UTF8.GetBytes("{\"signed\":{\"text\":\"éĀ😀\"}}"));
        Assert.Equal("{\"text\":\"éĀ😀\"}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void VerificationRejectsDuplicatePropertiesAndCultureSpecificExpiry()
    {
        using var fixture = new RegistryFixture();
        var duplicate = fixture.InitialRoot.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(duplicate, fixture.InitialTargets));
        var root = JsonNode.Parse(fixture.InitialRoot)!;
        root["signed"]!["expires"] = "12/31/2099";
        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(Sign(root, (TufTestSigner.TestKeyId, fixture.Key)), fixture.InitialTargets));
    }

    private static JsonNode NextRoot(string original, ECDsa key, int version)
    {
        var envelope = JsonNode.Parse(original)!;
        var signed = envelope["signed"]!;
        signed["version"] = version;
        signed["keys"] = new JsonObject
        {
            ["next-key"] = new JsonObject
            {
                ["keytype"] = "ecdsa-sha2-nistp256",
                ["keyval"] = new JsonObject { ["public"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }
            }
        };
        signed["roles"] = new JsonObject
        {
            ["root"] = new JsonObject { ["keyids"] = new JsonArray("next-key"), ["threshold"] = 1 },
            ["targets"] = new JsonObject { ["keyids"] = new JsonArray("next-key"), ["threshold"] = 1 }
        };
        return envelope;
    }

    private static string Sign(JsonNode envelope, params (string Id, ECDsa Key)[] signers)
    {
        var canonical = TufCanonicalJson.EncodeSignedPortion(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        envelope["signatures"] = new JsonArray(signers.Select(signer => (JsonNode)new JsonObject
        {
            ["keyid"] = signer.Id,
            ["sig"] = Convert.ToBase64String(signer.Key.SignData(canonical, HashAlgorithmName.SHA256))
        }).ToArray());
        return envelope.ToJsonString();
    }

    private sealed class RegistryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vela-trust-tests", Guid.NewGuid().ToString("N"));
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Registry => Path.Combine(Root, "registry");
        public string Anchor => Path.Combine(Root, "out-of-band-root.json");
        public VelaPackageCache Cache { get; }
        public string InitialRoot { get; }
        public string InitialTargets { get; }

        public RegistryFixture()
        {
            Cache = new VelaPackageCache(Path.Combine(Root, "cache"));
            var source = Path.Combine(Root, "source");
            Directory.CreateDirectory(Path.Combine(source, "src"));
            File.WriteAllText(Path.Combine(source, "vela.toml"), "[package]\nname=\"acme.demo\"\nversion=\"1.0.0\"\nkind=\"source-library\"\n");
            File.WriteAllText(Path.Combine(source, "src", "lib.vela"), "original");
            var archive = Path.Combine(Registry, "acme.demo", "1.0.0", "package.vlpkg");
            VelaDependencyArchive.Pack(source, archive);
            Directory.CreateDirectory(Path.Combine(Registry, "tuf"));
            (InitialRoot, InitialTargets) = TufTestSigner.SignTargets(Key, Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()),
                new Dictionary<string, TufTargetFile> { ["acme.demo/1.0.0/package.vlpkg"] = new(new FileInfo(archive).Length, new TufTargetHashes(TufVerifier.ComputeSha256Hex(archive))) });
            File.WriteAllText(Anchor, InitialRoot);
            File.WriteAllText(MetadataPath("root.json"), InitialRoot);
            File.WriteAllText(MetadataPath("targets.json"), InitialTargets);
        }

        public string MetadataPath(string filename) => Path.Combine(Registry, "tuf", filename);
        public Uri Uri(bool http) => http ? new Uri("https://registry.invalid/") : new Uri(Registry);
        public Task<VelaRestoredPackage> Restore(VelaRestoreClient client, bool http = false) => client.RestoreAsync("acme.demo", "1.0.0", Uri(http).AbsoluteUri);
        public VelaRestoreClient Client(bool pin = true, bool allowUnsigned = false, bool http = false) => new(Cache,
            http ? new ResponseHandler(request =>
            {
                var relative = request.RequestUri!.AbsolutePath.TrimStart('/');
                if (relative.StartsWith("packages/", StringComparison.Ordinal)) { relative = relative[9..]; }
                var path = Path.Combine(Registry, relative.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }) : null, new VelaRestoreOptions { TrustedRootPath = pin ? Anchor : null, AllowUnsigned = allowUnsigned });
        public void WriteTargets(int version, ECDsa? key = null, string keyId = TufTestSigner.TestKeyId, Action<JsonNode>? edit = null)
        {
            var targets = JsonNode.Parse(InitialTargets)!;
            targets["signed"]!["version"] = version;
            edit?.Invoke(targets["signed"]!);
            File.WriteAllText(MetadataPath("targets.json"), Sign(targets, (keyId, key ?? Key)));
        }
        public void Dispose() { Key.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
