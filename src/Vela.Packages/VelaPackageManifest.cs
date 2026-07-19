using System.Text.Json.Serialization;

namespace Vela.Packages;

/// <summary>Package kind as declared in a manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VelaPackageKind>))]
public enum VelaPackageKind
{
    /// <summary>Executable application package.</summary>
    Application,

    /// <summary>Native library package.</summary>
    Library,

    /// <summary>Source-linked library package.</summary>
    SourceLibrary
}

/// <summary>Remote or cached package manifest metadata.</summary>
public sealed record VelaPackageManifest(
    string Name,
    string Version,
    VelaPackageKind Kind,
    IReadOnlyDictionary<string, string> Dependencies)
{
    /// <summary>Creates a manifest from a local <c>vela.toml</c> path.</summary>
    public static VelaPackageManifest FromToml(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest '{manifestPath}' was not found.", manifestPath);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = string.Empty;

        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = Unquote(line[(equals + 1)..].Trim());
            if (section == "package")
            {
                values[key] = value;
            }
            else if (section == "dependencies")
            {
                dependencies[key] = value;
            }
        }

        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException($"Manifest '{manifestPath}' is missing [package].name.");
        }

        if (!values.TryGetValue("version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException($"Manifest '{manifestPath}' is missing [package].version.");
        }

        var kind = ParseKind(values.GetValueOrDefault("kind", "application"));
        return new VelaPackageManifest(name, version, kind, dependencies);
    }

    private static VelaPackageKind ParseKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "application" => VelaPackageKind.Application,
        "library" => VelaPackageKind.Library,
        "source-library" => VelaPackageKind.SourceLibrary,
        _ => throw new InvalidDataException($"Unsupported package kind '{kind}'.")
    };

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash >= 0 ? line[..hash] : line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }
}
