using Vela.Packages;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class RegistryClientTests
{
    [Fact]
    public async Task FileRegistry_RestoreArchiveRoundtrip()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var registryRoot = Path.Combine(root, "registry");
        var packageRoot = Path.Combine(root, "source", "acme.demo");
        Directory.CreateDirectory(Path.Combine(packageRoot, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "vela.toml"),
            """
            [package]
            name = "acme.demo"
            version = "1.2.0"
            kind = "source-library"
            """);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "src", "lib.vela"), "pub fn ping() -> str { \"pong\" }");

        var archivePath = Path.Combine(root, "acme.demo.vlpkg");
        VelaDependencyArchive.Pack(packageRoot, archivePath);

        var versionDirectory = Path.Combine(registryRoot, "acme.demo", "1.2.0");
        Directory.CreateDirectory(versionDirectory);
        File.Copy(archivePath, Path.Combine(versionDirectory, "package.vlpkg"));

        using var client = new VelaRestoreClient(new VelaPackageCache(cacheRoot));
        var restored = await client.RestoreAsync("acme.demo", "1.2.0", new Uri(registryRoot).AbsoluteUri);

        Assert.Equal("acme.demo", restored.Name);
        Assert.Equal("1.2.0", restored.Version);
        Assert.Equal("acme.demo", restored.Manifest.Name);
        Assert.True(File.Exists(Path.Combine(restored.Path, "src", "lib.vela")));
        Assert.Contains("pong", await File.ReadAllTextAsync(Path.Combine(restored.Path, "src", "lib.vela")), StringComparison.Ordinal);

        var cachedAgain = await client.RestoreAsync("acme.demo", "^1.2.0", new Uri(registryRoot).AbsoluteUri);
        Assert.Equal(restored.Path, cachedAgain.Path);
    }

    [Fact]
    public async Task FileRegistry_RestoreExtractedFolder()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var registryRoot = Path.Combine(root, "registry");
        var versionDirectory = Path.Combine(registryRoot, "acme.local", "2.0.0");
        Directory.CreateDirectory(Path.Combine(versionDirectory, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, "vela.toml"),
            """
            [package]
            name = "acme.local"
            version = "2.0.0"
            kind = "library"
            """);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, "src", "lib.vela"), "pub fn ok() -> bool { true }");

        using var client = new VelaRestoreClient(new VelaPackageCache(cacheRoot));
        var restored = await client.RestoreAsync("acme.local", ">=2.0.0", new Uri(registryRoot).AbsoluteUri);

        Assert.Equal("2.0.0", restored.Version);
        Assert.True(File.Exists(Path.Combine(restored.Path, "vela.toml")));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("10.0.0-beta.1", 10, 0, 0)]
    public void SemVer_Parse(string text, int major, int minor, int patch)
    {
        var version = SemVer.Parse(text);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Fact]
    public void SemVer_CompareAndSatisfies()
    {
        var left = SemVer.Parse("1.2.3");
        var right = SemVer.Parse("1.2.4");
        Assert.True(SemVer.Compare(left, right) < 0);
        Assert.True(SemVer.Satisfies(SemVer.Parse("1.2.5"), "^1.2.0"));
        Assert.False(SemVer.Satisfies(SemVer.Parse("2.0.0"), "^1.2.0"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vela-packages-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
