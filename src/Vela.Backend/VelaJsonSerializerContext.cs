using System.Text.Json.Serialization;
using Vela.Backend.Capabilities;

namespace Vela.Backend;

/// <summary>Provides trim-safe JSON metadata for compiler-owned file formats.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VelaAbiManifest))]
[JsonSerializable(typeof(VelaPackageLockDocument))]
[JsonSerializable(typeof(VelaPackageArchiveManifest))]
[JsonSerializable(typeof(VelaRegistryCredentialsDocument))]
[JsonSerializable(typeof(VelaCapabilityCatalogDocument))]
[JsonSerializable(typeof(VelaCapability))]
[JsonSerializable(typeof(VelaManagedPackage))]
[JsonSerializable(typeof(IReadOnlyList<VelaCapability>))]
[JsonSerializable(typeof(IReadOnlyList<VelaManagedPackage>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class VelaJsonSerializerContext : JsonSerializerContext
{
}
