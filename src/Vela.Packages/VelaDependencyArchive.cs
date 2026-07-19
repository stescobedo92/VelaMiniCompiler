using System.IO.Compression;
using System.Text;

namespace Vela.Packages;

/// <summary>Thrown when packing or unpacking a <c>.vlpkg</c> archive fails.</summary>
public sealed class VelaDependencyArchiveException(string message) : Exception(message);

/// <summary>Packs and unpacks Vela package directories as <c>.vlpkg</c> zip archives.</summary>
public static class VelaDependencyArchive
{
    /// <summary>The file extension used by Vela package archives.</summary>
    public const string Extension = ".vlpkg";

    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a <c>.vlpkg</c> archive from a package directory containing <c>vela.toml</c>.</summary>
    public static string Pack(string packageDirectory, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var root = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(root, "vela.toml");
        if (!File.Exists(manifestPath))
        {
            throw new VelaDependencyArchiveException($"Package directory '{root}' does not contain vela.toml.");
        }

        var files = CollectFiles(root);
        var destination = outputPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(outputPath)
            : Path.GetFullPath(Path.Combine(outputPath, $"{Path.GetFileName(root)}{Extension}"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var (fullPath, relativePath) in files)
        {
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using var entryStream = entry.Open();
            using var source = File.OpenRead(fullPath);
            source.CopyTo(entryStream);
        }

        return destination;
    }

    /// <summary>Extracts a <c>.vlpkg</c> archive into <paramref name="destinationDirectory"/>.</summary>
    public static string Unpack(string archivePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!File.Exists(archivePath))
        {
            throw new VelaDependencyArchiveException($"Archive '{archivePath}' was not found.");
        }

        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new VelaDependencyArchiveException($"Archive entry '{relative}' escapes the destination directory.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new VelaDependencyArchiveException($"Archive entry '{relative}' escapes the destination directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        var manifestPath = Path.Combine(destination, "vela.toml");
        if (!File.Exists(manifestPath))
        {
            throw new VelaDependencyArchiveException($"Archive '{archivePath}' does not contain vela.toml.");
        }

        return destination;
    }

    private static List<(string FullPath, string RelativePath)> CollectFiles(string rootDirectory)
    {
        var files = new List<(string, string)> { (Path.Combine(rootDirectory, "vela.toml"), "vela.toml") };
        var sourceDirectory = Path.Combine(rootDirectory, "src");
        if (Directory.Exists(sourceDirectory))
        {
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                files.Add((source, Path.GetRelativePath(rootDirectory, source).Replace('\\', '/')));
            }
        }

        foreach (var optional in new[] { "README.md", "LICENSE", "LICENSE.md", "LICENSE.txt" })
        {
            var candidate = Path.Combine(rootDirectory, optional);
            if (File.Exists(candidate))
            {
                files.Add((candidate, optional));
            }
        }

        return files;
    }
}
