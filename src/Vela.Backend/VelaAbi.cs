using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Backend;

/// <summary>Describes one native symbol exported by a Vela shared library.</summary>
public sealed record VelaFfiExport(string Name, string Symbol, IReadOnlyList<string> Parameters, string ReturnType);

/// <summary>Identifies a locked native Vela library made available to one consuming compilation.</summary>
public sealed record VelaLibraryImport(string PackageName, string LibraryPath, VelaAbiManifest Manifest)
{
    /// <summary>Gets the linker name used by generated platform-native import declarations.</summary>
    public string LibraryName
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(Manifest.LibraryFileName);
            var extension = Path.GetExtension(Manifest.LibraryFileName);
            return extension is ".so" or ".dylib" && name.StartsWith("lib", StringComparison.Ordinal)
                ? name[3..]
                : name;
        }
    }
}

/// <summary>Versioned manifest consumed by Vela applications before loading a native package.</summary>
public sealed record VelaAbiManifest(
    int AbiVersion,
    string Package,
    string Version,
    string RuntimeIdentifier,
    string LibraryFileName,
    IReadOnlyList<VelaFfiExport> Exports,
    string ContractHash)
{
    /// <summary>Creates a canonical ABI manifest with a content-derived contract hash.</summary>
    /// <param name="abiVersion">Use 2 for status/out-buffer Text/Decimal exports; 1 for legacy direct returns.</param>
    public static VelaAbiManifest Create(
        string package,
        string version,
        string runtimeIdentifier,
        string libraryFileName,
        IReadOnlyList<VelaFfiExport> exports,
        int abiVersion = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFileName);
        ArgumentNullException.ThrowIfNull(exports);
        if (abiVersion is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(abiVersion), "ABI version must be 1 or 2.");
        }

        var versionText = abiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var canonicalExports = exports
            .OrderBy(static exportItem => exportItem.Name, StringComparer.Ordinal)
            .ThenBy(static exportItem => exportItem.Symbol, StringComparer.Ordinal)
            .Select(static exportItem => $"{exportItem.Name}|{exportItem.Symbol}|{string.Join(",", exportItem.Parameters)}|{exportItem.ReturnType}");
        var canonical = string.Join("\n", new[] { "vela-native-abi", versionText, package, version, runtimeIdentifier, libraryFileName }.Concat(canonicalExports));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new VelaAbiManifest(abiVersion, package, version, runtimeIdentifier, libraryFileName, exports, hash);
    }

    /// <summary>Writes the manifest as indented UTF-8 JSON.</summary>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(this, VelaJsonSerializerContext.Default.VelaAbiManifest) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>Represents generated C# and exports for a Vela shared-library package.</summary>
public sealed record VelaLibraryEmission(string GeneratedSource, IReadOnlyList<VelaFfiExport> Exports);
