using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vela.Backend;

/// <summary>Identifies the artifact produced by a Vela package.</summary>
public enum VelaPackageKind
{
    /// <summary>Builds an executable from <c>src/main.vela</c>.</summary>
    Application,

    /// <summary>Builds a Native AOT shared library from <c>src/lib.vela</c>.</summary>
    Library,

    /// <summary>Links <c>src/lib.vela</c> directly into a consuming Vela application.</summary>
    SourceLibrary
}

/// <summary>Declares one deterministic local package dependency.</summary>
public sealed record VelaPackageDependency(string Name, string Path);

/// <summary>Represents the subset of <c>vela.toml</c> accepted by the first Vela package resolver.</summary>
public sealed record VelaPackageManifest(
    string Name,
    string Version,
    VelaPackageKind Kind,
    string RootDirectory,
    string ManifestPath,
    IReadOnlyList<VelaPackageDependency> Dependencies)
{
    /// <summary>Gets the conventional Vela entry source path for this package.</summary>
    public string EntryPointPath => Path.Combine(RootDirectory, "src", Kind is VelaPackageKind.Library or VelaPackageKind.SourceLibrary ? "lib.vela" : "main.vela");
}

/// <summary>Represents a fully validated package graph with deterministic dependency ordering.</summary>
public sealed record VelaPackageGraph(VelaPackageManifest Root, IReadOnlyList<VelaPackageManifest> Packages, string LockFilePath)
{
    /// <summary>Gets dependencies in build order, followed by the root package.</summary>
    public IEnumerable<VelaPackageManifest> BuildOrder => Packages;
}

/// <summary>Thrown when a Vela project manifest or dependency graph is invalid.</summary>
public sealed class VelaPackageException(string message) : Exception(message)
{
}

/// <summary>Loads Vela manifests, validates local path dependencies, and writes deterministic lockfiles.</summary>
public sealed partial class VelaPackageResolver
{
    private const string ManifestFileName = "vela.toml";
    private static readonly Regex PackageNamePattern = PackageNameRegex();
    private static readonly Regex KeyValuePattern = KeyValueRegex();
    private static readonly Regex DependencyPattern = DependencyRegex();

    /// <summary>Resolves a package directory or an explicit manifest path.</summary>
    public static VelaPackageGraph Resolve(string packagePath, bool writeLockFile = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var rootManifestPath = ResolveManifestPath(packagePath);
        var states = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<VelaPackageManifest>();
        var manifestsByPath = new Dictionary<string, VelaPackageManifest>(StringComparer.OrdinalIgnoreCase);

        Visit(rootManifestPath, states, ordered, manifestsByPath);
        var root = manifestsByPath[rootManifestPath];
        var graph = new VelaPackageGraph(root, ordered, Path.Combine(root.RootDirectory, "vela.lock"));
        if (writeLockFile)
        {
            WriteLockFile(graph);
        }

        return graph;
    }

    /// <summary>Loads and validates one Vela manifest without resolving its dependencies.</summary>
    public static VelaPackageManifest LoadManifest(string packagePath)
    {
        var manifestPath = ResolveManifestPath(packagePath);
        var rootDirectory = Path.GetDirectoryName(manifestPath) ?? throw new VelaPackageException("A manifest must be contained in a directory.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var dependencies = new List<VelaPackageDependency>();
        var dependencyNames = new HashSet<string>(StringComparer.Ordinal);
        var section = string.Empty;

        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            lineNumber++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                if (section is not "package" and not "dependencies")
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: unsupported manifest section '[{section}]'.");
                }

                continue;
            }

            if (section == "package")
            {
                var match = KeyValuePattern.Match(line);
                if (!match.Success)
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: expected 'key = \"value\"' in [package].");
                }

                var key = match.Groups["key"].Value;
                if (!values.TryAdd(key, match.Groups["value"].Value))
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: duplicate package key '{key}'.");
                }

                continue;
            }

            if (section == "dependencies")
            {
                var match = DependencyPattern.Match(line);
                if (!match.Success)
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: dependencies must use 'name = {{ path = \"relative/path\" }}'.");
                }

                var dependencyName = match.Groups["name"].Value;
                ValidatePackageName(dependencyName, manifestPath, lineNumber);
                if (!dependencyNames.Add(dependencyName))
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: duplicate dependency '{dependencyName}'.");
                }

                var relativePath = match.Groups["path"].Value;
                if (Path.IsPathRooted(relativePath))
                {
                    throw new VelaPackageException($"{manifestPath}:{lineNumber}: dependency '{dependencyName}' must use a relative path.");
                }

                dependencies.Add(new VelaPackageDependency(dependencyName, Path.GetFullPath(Path.Combine(rootDirectory, relativePath))));
                continue;
            }

            throw new VelaPackageException($"{manifestPath}:{lineNumber}: each value must be in [package] or [dependencies].");
        }

        if (!values.TryGetValue("name", out var name))
        {
            throw new VelaPackageException($"{manifestPath}: [package] requires 'name'.");
        }

        ValidatePackageName(name, manifestPath, null);
        if (!values.TryGetValue("version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            throw new VelaPackageException($"{manifestPath}: [package] requires a non-empty 'version'.");
        }

        if (!values.TryGetValue("kind", out var kindText) || !TryParseKind(kindText, out var kind))
        {
            throw new VelaPackageException($"{manifestPath}: [package].kind must be 'application', 'library', or 'source-library'.");
        }

        var entryPoint = Path.Combine(rootDirectory, "src", kind is VelaPackageKind.Library or VelaPackageKind.SourceLibrary ? "lib.vela" : "main.vela");
        if (!File.Exists(entryPoint))
        {
            throw new VelaPackageException($"{manifestPath}: package '{name}' requires entry source '{entryPoint}'.");
        }

        return new VelaPackageManifest(name, version, kind, rootDirectory, manifestPath, dependencies.OrderBy(static dependency => dependency.Name, StringComparer.Ordinal).ToArray());
    }

    private static void Visit(
        string manifestPath,
        IDictionary<string, VisitState> states,
        ICollection<VelaPackageManifest> ordered,
        IDictionary<string, VelaPackageManifest> manifestsByPath)
    {
        if (states.TryGetValue(manifestPath, out var state))
        {
            if (state == VisitState.Visiting)
            {
                throw new VelaPackageException($"Dependency cycle detected at '{manifestPath}'.");
            }

            return;
        }

        states[manifestPath] = VisitState.Visiting;
        var manifest = LoadManifest(manifestPath);
        manifestsByPath.Add(manifestPath, manifest);
        foreach (var dependency in manifest.Dependencies)
        {
            var dependencyManifestPath = ResolveManifestPath(dependency.Path);
            var dependencyManifest = LoadManifest(dependencyManifestPath);
            if (!string.Equals(dependency.Name, dependencyManifest.Name, StringComparison.Ordinal))
            {
                throw new VelaPackageException($"Dependency '{dependency.Name}' in '{manifest.ManifestPath}' points to package '{dependencyManifest.Name}'.");
            }

            Visit(dependencyManifestPath, states, ordered, manifestsByPath);
        }

        states[manifestPath] = VisitState.Visited;
        ordered.Add(manifest);
    }

    private static string ResolveManifestPath(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var manifestPath = Directory.Exists(fullPath)
            ? Path.Combine(fullPath, ManifestFileName)
            : fullPath;
        if (!File.Exists(manifestPath) || !string.Equals(Path.GetFileName(manifestPath), ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new VelaPackageException($"Vela manifest '{manifestPath}' was not found.");
        }

        return manifestPath;
    }

    private static void WriteLockFile(VelaPackageGraph graph)
    {
        var entries = graph.Packages
            .OrderBy(static package => package.Name, StringComparer.Ordinal)
            .ThenBy(static package => package.RootDirectory, StringComparer.Ordinal)
            .Select(package => new VelaPackageLockPackage(
                package.Name,
                package.Version,
                FormatKind(package.Kind),
                Path.GetRelativePath(graph.Root.RootDirectory, package.RootDirectory).Replace('\\', '/'),
                HashManifest(package.ManifestPath)))
            .ToArray();
        var content = JsonSerializer.Serialize(
            new VelaPackageLockDocument(1, entries),
            VelaJsonSerializerContext.Default.VelaPackageLockDocument) + Environment.NewLine;
        File.WriteAllText(graph.LockFilePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string HashManifest(string manifestPath)
    {
        var bytes = File.ReadAllBytes(manifestPath);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string StripComment(string value)
    {
        var inQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"' && (index == 0 || value[index - 1] != '\\'))
            {
                inQuote = !inQuote;
            }
            else if (value[index] == '#' && !inQuote)
            {
                return value[..index];
            }
        }

        return value;
    }

    private static bool TryParseKind(string value, out VelaPackageKind kind)
    {
        kind = value switch
        {
            "application" => VelaPackageKind.Application,
            "library" => VelaPackageKind.Library,
            "source-library" => VelaPackageKind.SourceLibrary,
            _ => default
        };
        return value is "application" or "library" or "source-library";
    }

    private static string FormatKind(VelaPackageKind kind) => kind switch
    {
        VelaPackageKind.Application => "application",
        VelaPackageKind.Library => "library",
        VelaPackageKind.SourceLibrary => "source-library",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ValidatePackageName(string name, string manifestPath, int? lineNumber)
    {
        if (!PackageNamePattern.IsMatch(name))
        {
            var location = lineNumber is null ? manifestPath : $"{manifestPath}:{lineNumber}";
            throw new VelaPackageException($"{location}: package name '{name}' is invalid.");
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex("^(?<key>[A-Za-z][A-Za-z0-9_-]*)\\s*=\\s*\\\"(?<value>[^\\\"]+)\\\"$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex("^(?<name>[A-Za-z][A-Za-z0-9._-]*)\\s*=\\s*\\{\\s*path\\s*=\\s*\\\"(?<path>[^\\\"]+)\\\"\\s*\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyRegex();

    private enum VisitState
    {
        Visiting,
        Visited
    }

}

/// <summary>Represents the stable JSON shape written to a package lock file.</summary>
internal sealed record VelaPackageLockDocument(int LockVersion, IReadOnlyList<VelaPackageLockPackage> Packages);

/// <summary>Represents one package entry in a package lock file.</summary>
internal sealed record VelaPackageLockPackage(string Name, string Version, string Kind, string Path, string ManifestHash);
