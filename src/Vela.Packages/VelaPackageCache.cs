using System.Security.Cryptography;
using System.Text;

namespace Vela.Packages;

/// <summary>On-disk cache for restored package trees.</summary>
public sealed class VelaPackageCache
{
    /// <summary>Initializes a cache at the default user profile location.</summary>
    public VelaPackageCache()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vela", "cache"))
    {
    }

    /// <summary>Initializes a cache at an explicit root directory.</summary>
    public VelaPackageCache(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Gets the cache root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Gets an isolated cache for an absolute registry URI.</summary>
    /// <remarks>
    /// Existing entries without a registry identity are not reused. URI path case and
    /// trailing slashes are preserved because they can select different HTTP resources.
    /// </remarks>
    public VelaPackageCache ForRegistry(Uri registryUri)
    {
        ArgumentNullException.ThrowIfNull(registryUri);
        if (!registryUri.IsAbsoluteUri)
        {
            throw new ArgumentException("A registry URI must be absolute.", nameof(registryUri));
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registryUri.AbsoluteUri)))
            .ToLowerInvariant();
        return new VelaPackageCache(Path.Combine(RootDirectory, "registries", sourceHash));
    }

    /// <summary>Gets the extracted package directory for <paramref name="name"/> at <paramref name="version"/>.</summary>
    public string GetPackagePath(string name, string version) =>
        Path.Combine(RootDirectory, "packages", Sanitize(name), Sanitize(version));

    /// <summary>Gets the cached archive path for <paramref name="name"/> at <paramref name="version"/>.</summary>
    public string GetArchivePath(string name, string version) =>
        Path.Combine(RootDirectory, "archives", Sanitize(name), $"{Sanitize(version)}{VelaDependencyArchive.Extension}");

    /// <summary>Stores an extracted package tree, replacing any previous copy.</summary>
    public string StoreExtracted(string name, string version, string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var destination = GetPackagePath(name, version);
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        CopyDirectory(sourceDirectory, destination);
        return destination;
    }

    /// <summary>Stores a downloaded archive in the cache.</summary>
    public string StoreArchive(string name, string version, string sourceArchivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        var destination = GetArchivePath(name, version);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourceArchivePath, destination, overwrite: true);
        return destination;
    }

    /// <summary>Returns whether an extracted package already exists in the cache.</summary>
    public bool HasExtracted(string name, string version)
    {
        var path = GetPackagePath(name, version);
        return File.Exists(Path.Combine(path, "vela.toml"));
    }

    private static string Sanitize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.Any(static character => char.IsControl(character)
                || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        {
            throw new ArgumentException("Cache identities must be safe, single path components.", nameof(value));
        }
        return value;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
