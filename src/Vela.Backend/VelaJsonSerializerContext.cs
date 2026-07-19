using System.Text.Json.Serialization;

namespace Vela.Backend;

/// <summary>Provides trim-safe JSON metadata for compiler-owned file formats.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VelaAbiManifest))]
[JsonSerializable(typeof(VelaPackageLockDocument))]
[JsonSerializable(typeof(VelaPackageArchiveManifest))]
[JsonSerializable(typeof(VelaRegistryCredentialsDocument))]
internal sealed partial class VelaJsonSerializerContext : JsonSerializerContext
{
}
