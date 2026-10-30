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

    /// <summary>The maximum total number of uncompressed bytes in a package archive.</summary>
    public const long MaximumUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>The maximum number of entries, including implicit parent directories, in a package archive.</summary>
    public const int MaximumEntryCount = 10_000;

    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a <c>.vlpkg</c> archive from a package directory containing <c>vela.toml</c>.</summary>
    public static string Pack(string packageDirectory, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        EnsureNoLinks(root, root);
        var manifestPath = Path.Combine(root, "vela.toml");
        EnsureNoLinks(manifestPath, root);
        if (!File.Exists(manifestPath))
        {
            throw new VelaDependencyArchiveException($"Package directory '{root}' does not contain vela.toml.");
        }

        var files = CollectFiles(root);
        var destination = outputPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(outputPath)
            : Path.GetFullPath(Path.Combine(outputPath, $"{Path.GetFileName(root)}{Extension}"));
        EnsureNoLinks(destination, Path.GetDirectoryName(destination)!);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        files.RemoveAll(file => string.Equals(file.FullPath, destination, pathComparison));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var (fullPath, relativePath) in files)
        {
            EnsureNoLinks(fullPath, root);
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using var entryStream = entry.Open();
            using var source = File.OpenRead(fullPath);
            source.CopyTo(entryStream);
        }

        return destination;
    }

    /// <summary>Extracts a <c>.vlpkg</c> archive into <paramref name="destinationDirectory"/>.</summary>
    public static string Unpack(string archivePath, string destinationDirectory) =>
        Unpack(archivePath, destinationDirectory, MaximumUncompressedBytes, MaximumEntryCount);

    /// <summary>Extracts an archive while enforcing entry count and total uncompressed byte limits.</summary>
    /// <remarks>The destination and its ancestors must remain controlled by the caller throughout extraction.
    /// The entry limit includes implicit parent directories.</remarks>
    public static string Unpack(string archivePath, string destinationDirectory, long maxUncompressedBytes, int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        if (!File.Exists(archivePath))
        {
            throw new VelaDependencyArchiveException($"Archive '{archivePath}' was not found.");
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        EnsureNoLinks(destination, destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = ValidateEntries(archive, destination, maxUncompressedBytes, maxEntries);
        Directory.CreateDirectory(destination);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        foreach (var (entry, target, isDirectory) in entries)
        {
            EnsureNoLinks(target, destination);
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, $".vela-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var source = entry.Open())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long entryBytes = 0;
                    int read;
                    while ((read = source.Read(buffer)) != 0)
                    {
                        if (read > maxUncompressedBytes - totalBytes || read > entry.Length - entryBytes)
                        {
                            throw new VelaDependencyArchiveException("Archive exceeds its uncompressed byte limit or declared entry length.");
                        }

                        output.Write(buffer, 0, read);
                        totalBytes += read;
                        entryBytes += read;
                    }

                    if (entryBytes != entry.Length)
                    {
                        throw new VelaDependencyArchiveException($"Archive entry '{entry.FullName}' has an invalid length.");
                    }
                }

                EnsureNoLinks(target, destination);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return destination;
    }

    private static List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)> ValidateEntries(
        ZipArchive archive, string destination, long maxUncompressedBytes, int maxEntries)
    {
        if (archive.Entries.Count > maxEntries)
        {
            throw new VelaDependencyArchiveException($"Archive exceeds the limit of {maxEntries} entries.");
        }

        var entries = new List<(ZipArchiveEntry, string, bool)>(archive.Entries.Count);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var destinationPrefix = Path.EndsInDirectorySeparator(destination) ? destination : destination + Path.DirectorySeparatorChar;
        long declaredBytes = 0;
        var hasManifest = false;
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (unixType != 0 && unixType != 0x8000 && unixType != 0x4000))
            {
                throw new VelaDependencyArchiveException($"Archive entry '{entry.FullName}' is a link or special file.");
            }

            var relative = entry.FullName.Replace('\\', '/');
            var isDirectory = relative.EndsWith('/');
            relative = isDirectory ? relative[..^1] : relative;
            ValidateRelativePath(relative);
            if (!explicitPaths.Add(relative)
                || (paths.TryGetValue(relative, out var existingDirectory) && (!existingDirectory || !isDirectory)))
            {
                throw new VelaDependencyArchiveException($"Archive entry '{relative}' has a duplicate or conflicting path.");
            }

            paths[relative] = isDirectory;
            for (var slash = relative.LastIndexOf('/'); slash >= 0; slash = relative.LastIndexOf('/', slash - 1))
            {
                var parent = relative[..slash];
                if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                {
                    throw new VelaDependencyArchiveException($"Archive entry '{relative}' conflicts with a file path.");
                }

                paths[parent] = true;
            }

            if (paths.Count > maxEntries)
            {
                throw new VelaDependencyArchiveException($"Archive exceeds the limit of {maxEntries} files and directories.");
            }

            if (entry.Length < 0 || entry.Length > maxUncompressedBytes - declaredBytes || (isDirectory && entry.Length != 0))
            {
                throw new VelaDependencyArchiveException("Archive exceeds its uncompressed byte limit or has an invalid directory entry.");
            }

            declaredBytes += entry.Length;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destinationPrefix, StringComparison.Ordinal))
            {
                throw new VelaDependencyArchiveException($"Archive entry '{relative}' escapes the destination directory.");
            }

            EnsureNoLinks(target, destination);
            entries.Add((entry, target, isDirectory));
            hasManifest |= !isDirectory && relative == "vela.toml";
        }

        if (!hasManifest)
        {
            throw new VelaDependencyArchiveException("Archive does not contain vela.toml.");
        }

        return entries;
    }

    private static void ValidateRelativePath(string relative)
    {
        // Bound both filesystem work and the memory used to track parent paths.
        if (Encoding.UTF8.GetByteCount(relative) > 1024)
        {
            throw new VelaDependencyArchiveException("Archive entry path exceeds the maximum length of 1024 bytes.");
        }

        var segments = relative.Split('/');
        if (segments.Length > 64)
        {
            throw new VelaDependencyArchiveException("Archive entry path exceeds the maximum depth of 64 segments.");
        }

        foreach (var segment in segments)
        {
            var deviceName = segment.Split('.')[0].TrimEnd(' ');
            var isDevice = deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || (deviceName.Length == 4
                    && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789\u00b9\u00b2\u00b3".Contains(deviceName[3], StringComparison.Ordinal));
            if (segment.Length == 0 || Encoding.UTF8.GetByteCount(segment) > 255
                || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character, StringComparison.Ordinal)) || isDevice)
            {
                throw new VelaDependencyArchiveException($"Archive entry '{relative}' has an invalid path.");
            }
        }
    }

    private static void EnsureNoLinks(string path, string boundary)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new VelaDependencyArchiveException($"Package path '{current}' is a symbolic link or reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
                // Missing descendants are created only after their existing ancestors are checked.
            }
            catch (DirectoryNotFoundException)
            {
                // Continue checking existing ancestors of a destination that has not been created.
            }

            if (string.Equals(current, boundary, StringComparison.Ordinal))
            {
                // Ancestors above the caller-controlled root can be system links, such as /var on macOS.
                break;
            }
        }
    }

    private static List<(string FullPath, string RelativePath)> CollectFiles(string rootDirectory)
    {
        var files = new List<(string, string)> { (Path.Combine(rootDirectory, "vela.toml"), "vela.toml") };
        var sourceDirectory = Path.Combine(rootDirectory, "src");
        EnsureNoLinks(sourceDirectory, rootDirectory);
        if (Directory.Exists(sourceDirectory))
        {
            var sources = new List<(string FullPath, string RelativePath)>();
            var directories = new Stack<string>();
            directories.Push(sourceDirectory);
            while (directories.TryPop(out var directory))
            {
                foreach (var source in Directory.EnumerateFileSystemEntries(directory))
                {
                    EnsureNoLinks(source, rootDirectory);
                    if (Directory.Exists(source))
                    {
                        directories.Push(source);
                    }
                    else
                    {
                        var relative = Path.GetRelativePath(rootDirectory, source).Replace('\\', '/');
                        ValidateRelativePath(relative);
                        sources.Add((source, relative));
                    }
                }
            }

            files.AddRange(sources.OrderBy(file => file.FullPath, StringComparer.Ordinal));
        }

        foreach (var optional in new[] { "README.md", "LICENSE", "LICENSE.md", "LICENSE.txt" })
        {
            var candidate = Path.Combine(rootDirectory, optional);
            EnsureNoLinks(candidate, rootDirectory);
            if (File.Exists(candidate))
            {
                files.Add((candidate, optional));
            }
        }

        return files;
    }
}
