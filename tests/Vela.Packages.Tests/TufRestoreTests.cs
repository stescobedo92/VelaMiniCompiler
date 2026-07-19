using Vela.Packages;
using Vela.Packages.Tuf;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class TufRestoreTests
{
    [Fact]
    public async Task FileRegistry_RestoreWithTufMetadataSucceeds()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var registryRoot = Path.Combine(root, "registry");
        var packageRoot = Path.Combine(root, "source", "dogfood.lib");
        Directory.CreateDirectory(Path.Combine(packageRoot, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "vela.toml"),
            """
            [package]
            name = "dogfood.lib"
            version = "0.1.0"
            kind = "source-library"
            """);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "src", "lib.vela"), "pub fn greet() -> str { \"hi\" }");

        var archivePath = Path.Combine(root, "dogfood.lib.vlpkg");
        VelaDependencyArchive.Pack(packageRoot, archivePath);

        var versionDirectory = Path.Combine(registryRoot, "dogfood.lib", "0.1.0");
        Directory.CreateDirectory(versionDirectory);
        File.Copy(archivePath, Path.Combine(versionDirectory, "package.vlpkg"));

        var (privateKey, publicKey) = TufTestSigner.CreateTestKey();
        var targetPath = TufVerifier.BuildArchiveTargetPath("dogfood.lib", "0.1.0");
        var targets = new Dictionary<string, TufTargetFile>
        {
            [targetPath] = new(
                new FileInfo(archivePath).Length,
                new TufTargetHashes(TufVerifier.ComputeSha256Hex(archivePath)))
        };
        var (rootJson, targetsJson) = TufTestSigner.SignTargets(privateKey, publicKey, targets);
        var tufDirectory = Path.Combine(registryRoot, "tuf");
        Directory.CreateDirectory(tufDirectory);
        await File.WriteAllTextAsync(Path.Combine(tufDirectory, "root.json"), rootJson);
        await File.WriteAllTextAsync(Path.Combine(tufDirectory, "targets.json"), targetsJson);

        using var client = new VelaRestoreClient(new VelaPackageCache(cacheRoot));
        var restored = await client.RestoreAsync("dogfood.lib", "0.1.0", new Uri(registryRoot).AbsoluteUri);

        Assert.Equal("dogfood.lib", restored.Name);
        Assert.Equal("0.1.0", restored.Version);
        Assert.Contains("hi", await File.ReadAllTextAsync(Path.Combine(restored.Path, "src", "lib.vela")), StringComparison.Ordinal);
        privateKey.Dispose();
    }

    [Fact]
    public async Task FileRegistry_RestoreRejectsArchiveWhenTufHashMismatch()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var registryRoot = Path.Combine(root, "registry");
        var versionDirectory = Path.Combine(registryRoot, "dogfood.lib", "0.1.0");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "package.vlpkg"), [1, 2, 3]);

        var (privateKey, publicKey) = TufTestSigner.CreateTestKey();
        var targetPath = TufVerifier.BuildArchiveTargetPath("dogfood.lib", "0.1.0");
        var targets = new Dictionary<string, TufTargetFile>
        {
            [targetPath] = new(3, new TufTargetHashes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"))
        };
        var (rootJson, targetsJson) = TufTestSigner.SignTargets(privateKey, publicKey, targets);
        var tufDirectory = Path.Combine(registryRoot, "tuf");
        Directory.CreateDirectory(tufDirectory);
        await File.WriteAllTextAsync(Path.Combine(tufDirectory, "root.json"), rootJson);
        await File.WriteAllTextAsync(Path.Combine(tufDirectory, "targets.json"), targetsJson);

        using var client = new VelaRestoreClient(new VelaPackageCache(cacheRoot));
        var error = await Assert.ThrowsAsync<VelaRegistryRestoreException>(
            () => client.RestoreAsync("dogfood.lib", "0.1.0", new Uri(registryRoot).AbsoluteUri));
        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        privateKey.Dispose();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vela-packages-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
