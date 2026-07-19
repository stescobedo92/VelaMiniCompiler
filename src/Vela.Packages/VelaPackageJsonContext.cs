using System.Text.Json.Serialization;

namespace Vela.Packages;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VelaPackageManifest))]
[JsonSerializable(typeof(VelaPackageKind))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class VelaPackageJsonContext : JsonSerializerContext;
