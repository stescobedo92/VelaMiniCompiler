using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vela.Packages;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class DependencyArchiveSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-archive-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("src/file:stream")]
    [InlineData("src//file")]
    [InlineData("src/./file")]
    [InlineData("src/file.")]
    [InlineData("src/file ")]
    [InlineData("src/NUL.txt")]
    [InlineData("src/con .txt")]
    [InlineData("src/COM1")]
    [InlineData("src/LPT\u00b9.txt")]
    [InlineData("src/CONIN$")]
    [InlineData("src/*.vela")]
    [InlineData("src/file\0.txt")]
    [InlineData("../")]
    public void Unpack_RejectsUnsafePathsBeforeWriting(string unsafePath)
    {
        var archive = CreateArchive(("vela.toml", "manifest"), (unsafePath, string.Empty));
        var destination = Path.Combine(_root, "extracted");

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination));

        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("src/file.vela", "src/file.vela")]
    [InlineData("src/file.vela", "src/FILE.vela")]
    [InlineData("src/file.vela", "src\\file.vela")]
    [InlineData("src", "src/file.vela")]
    [InlineData("src/file.vela", "src")]
    [InlineData("src/", "src/")]
    public void Unpack_RejectsDuplicateOrConflictingPaths(string first, string second)
    {
        var archive = CreateArchive(("vela.toml", "manifest"), (first, string.Empty), (second, string.Empty));

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, Path.Combine(_root, "extracted")));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData(unchecked((int)0x11FF0000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    public void Unpack_RejectsLinksAndSpecialEntries(int attributes)
    {
        var archive = CreateArchive(("vela.toml", "manifest"), ("src/link", "target"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("src/link")!.ExternalAttributes = attributes;
        }

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, Path.Combine(_root, "extracted")));
    }

    [Fact]
    public void Unpack_RejectsEntryCountIncludingDirectories()
    {
        var archive = CreateArchive(("vela.toml", "manifest"), ("src/", string.Empty), ("src/file", "content"));

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, Path.Combine(_root, "extracted"), 100, 2));
    }

    [Theory]
    [InlineData(1, 256)]
    [InlineData(65, 1)]
    [InlineData(8, 200)]
    public void Unpack_RejectsExcessivePathLengthOrDepth(int depth, int segmentLength)
    {
        var path = string.Join('/', Enumerable.Repeat(new string('a', segmentLength), depth));
        var archive = CreateArchive(("vela.toml", "manifest"), (path, "content"));
        var destination = Path.Combine(_root, "extracted");

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Unpack_CountsImplicitDirectoriesAgainstEntryLimit()
    {
        var archive = CreateArchive(("vela.toml", "manifest"), ("src/nested/file", "content"));

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, Path.Combine(_root, "extracted"), 100, 3));
    }

    [Fact]
    public void Unpack_RejectsAggregateUncompressedSizeBeforeWriting()
    {
        var archive = CreateArchive(("vela.toml", "12345"), ("src/file", "123456"));
        var destination = Path.Combine(_root, "extracted");

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination, 10, 2));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Unpack_DoesNotWritePastForgedUncompressedLength()
    {
        var archive = CreateArchive(("vela.toml", new string('x', 1024)));
        var bytes = File.ReadAllBytes(archive);
        var centralDirectory = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(centralDirectory >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(centralDirectory + 24, 4), 1);
        File.WriteAllBytes(archive, bytes);
        var destination = Path.Combine(_root, "extracted");

        // .NET also bounds its decompression stream to the declared length.
        VelaDependencyArchive.Unpack(archive, destination, 10, 1);

        Assert.Equal(1, new FileInfo(Path.Combine(destination, "vela.toml")).Length);
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void Unpack_RejectsTruncatedEntryWithoutPublishingIt()
    {
        var archive = CreateArchive(("vela.toml", "content"));
        var bytes = File.ReadAllBytes(archive);
        var centralDirectory = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(centralDirectory >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(centralDirectory + 24, 4), 100);
        File.WriteAllBytes(archive, bytes);
        var destination = Path.Combine(_root, "extracted");

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination, 100, 1));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void Unpack_RequiresManifestInArchiveEvenWhenDestinationHasOne()
    {
        var archive = CreateArchive(("src/file", "content"));
        var destination = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "vela.toml"), "old manifest");

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination));

        Assert.Equal("old manifest", File.ReadAllText(Path.Combine(destination, "vela.toml")));
        Assert.False(Directory.Exists(Path.Combine(destination, "src")));
    }

    [Fact]
    public void Unpack_AllowsExactLimitsNormalDirectoriesAndDotsInsideNames()
    {
        var archive = CreateArchive(("vela.toml", "12345"), ("src/file..vela", "12345"), ("src/", string.Empty));
        var destination = Path.Combine(_root, "extracted");

        Assert.Equal(destination, VelaDependencyArchive.Unpack(archive, destination, 10, 3));

        Assert.Equal("12345", File.ReadAllText(Path.Combine(destination, "src", "file..vela")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unpack_RejectsExistingDestinationLinks(bool directoryLink)
    {
        var archive = CreateArchive(("vela.toml", "manifest"), ("src/file", "replacement"));
        var destination = Path.Combine(_root, "extracted");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(destination, "src"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "file");
        File.WriteAllText(outsideFile, "original");
        if (directoryLink)
        {
            Directory.Delete(Path.Combine(destination, "src"));
            Directory.CreateSymbolicLink(Path.Combine(destination, "src"), outside);
        }
        else
        {
            File.CreateSymbolicLink(Path.Combine(destination, "src", "file"), outsideFile);
        }

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Unpack(archive, destination));

        Assert.Equal("original", File.ReadAllText(outsideFile));
        Assert.False(File.Exists(Path.Combine(destination, "vela.toml")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pack_RejectsSourceLinks(bool directoryLink)
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(package, "src"));
        File.WriteAllText(Path.Combine(package, "vela.toml"), "manifest");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret"), "secret");
        if (directoryLink)
        {
            Directory.CreateSymbolicLink(Path.Combine(package, "src", "link"), outside);
        }
        else
        {
            File.CreateSymbolicLink(Path.Combine(package, "src", "link"), Path.Combine(outside, "secret"));
        }

        Assert.Throws<VelaDependencyArchiveException>(() => VelaDependencyArchive.Pack(package, Path.Combine(_root, "package.vlpkg")));

        Assert.False(File.Exists(Path.Combine(_root, "package.vlpkg")));
    }

    [Fact]
    public void Pack_RoundTripsNestedSourcesAndOptionalFiles()
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(package, "src", "nested"));
        File.WriteAllText(Path.Combine(package, "vela.toml"), "manifest");
        File.WriteAllText(Path.Combine(package, "src", "nested", "lib.vela"), "source");
        File.WriteAllText(Path.Combine(package, "README.md"), "readme");
        var archive = VelaDependencyArchive.Pack(package, Path.Combine(_root, "package.vlpkg"));
        var destination = Path.Combine(_root, "extracted");

        VelaDependencyArchive.Unpack(archive, destination);

        Assert.Equal("source", File.ReadAllText(Path.Combine(destination, "src", "nested", "lib.vela")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(destination, "README.md")));
    }

    [Fact]
    public void PackAndUnpack_AllowTrustedSystemLinkAboveControlledRoots()
    {
        var physical = Path.Combine(_root, "physical");
        Directory.CreateDirectory(Path.Combine(physical, "package", "src"));
        File.WriteAllText(Path.Combine(physical, "package", "vela.toml"), "manifest");
        File.WriteAllText(Path.Combine(physical, "package", "src", "lib.vela"), "source");
        var systemLink = Path.Combine(_root, "system-link");
        Directory.CreateSymbolicLink(systemLink, physical);
        var archive = VelaDependencyArchive.Pack(Path.Combine(systemLink, "package"), Path.Combine(_root, "package.vlpkg"));

        VelaDependencyArchive.Unpack(archive, Path.Combine(systemLink, "extracted"));

        Assert.Equal("source", File.ReadAllText(Path.Combine(physical, "extracted", "src", "lib.vela")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "package.vlpkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }
}
