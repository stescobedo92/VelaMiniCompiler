using Vela.Backend;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaTestDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-test-discovery", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DiscoversTestFilesRecursivelyInOrder()
    {
        Write("b_test.vela", "fn main() -> Int {\n    return 0;\n}\n");
        Write("a.vela", "fn main() -> Int {\n    return 0;\n}\n");
        Write(Path.Combine("nested", "a_test.vela"), "fn main() -> Int {\n    return 0;\n}\n");
        Write(Path.Combine("nested", "notes.txt"), "not a test\n");

        var files = VelaTestDiscovery.FindTestFiles(_root);

        Assert.Equal(
            [Path.Combine(_root, "b_test.vela"), Path.Combine(_root, "nested", "a_test.vela")],
            files);
    }

    [Fact]
    public void SkipsTargetDirectories()
    {
        Write(Path.Combine("target", "debug", "cached_test.vela"), "fn main() -> Int {\n    return 0;\n}\n");
        Write("real_test.vela", "fn main() -> Int {\n    return 0;\n}\n");

        var files = VelaTestDiscovery.FindTestFiles(_root);

        Assert.Equal([Path.Combine(_root, "real_test.vela")], files);
    }

    [Fact]
    public void EmptyDirectoryReturnsNoFiles()
    {
        Directory.CreateDirectory(_root);

        Assert.Empty(VelaTestDiscovery.FindTestFiles(_root));
    }

    [Fact]
    public void MissingDirectoryThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => VelaTestDiscovery.FindTestFiles(Path.Combine(_root, "absent")));
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
