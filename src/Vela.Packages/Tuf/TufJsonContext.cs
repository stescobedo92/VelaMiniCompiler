using System.Text.Json.Serialization;

namespace Vela.Packages.Tuf;

/// <summary>Provides trim-safe JSON metadata for TUF root/targets envelopes.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TufRootMetadata))]
[JsonSerializable(typeof(TufTargetsMetadata))]
[JsonSerializable(typeof(Dictionary<string, TufPublicKey>))]
[JsonSerializable(typeof(Dictionary<string, TufRoleDefinition>))]
[JsonSerializable(typeof(Dictionary<string, TufTargetFile>))]
[JsonSerializable(typeof(IReadOnlyList<TufSignature>))]
internal sealed partial class TufJsonContext : JsonSerializerContext;
