using System.Text.Json.Serialization;

namespace Vela.Packages.Tuf;

/// <summary>One artifact entry in signed targets metadata.</summary>
public sealed record TufTargetFile(long Length, TufTargetHashes Hashes);

/// <summary>Supported content hashes for a TUF target.</summary>
public sealed record TufTargetHashes(string Sha256);

/// <summary>Public key material for a simplified single-role TUF root.</summary>
public sealed record TufPublicKey(string KeyType, TufKeyValue KeyVal);

/// <summary>Base64-encoded public key bytes.</summary>
public sealed record TufKeyValue(string Public);

/// <summary>Role delegation listing trusted key ids.</summary>
public sealed record TufRoleDefinition(IReadOnlyList<string> KeyIds, int Threshold);

/// <summary>Signed portion of root metadata.</summary>
public sealed record TufRootSigned(
    [property: JsonPropertyName("_type")] string Type,
    int Version,
    string Expires,
    IReadOnlyDictionary<string, TufPublicKey> Keys,
    IReadOnlyDictionary<string, TufRoleDefinition> Roles);

/// <summary>Root metadata envelope.</summary>
public sealed record TufRootMetadata(
    [property: JsonPropertyName("signed")] TufRootSigned SignedPayload,
    IReadOnlyList<TufSignature> Signatures);

/// <summary>Signed portion of targets metadata.</summary>
public sealed record TufTargetsSigned(
    [property: JsonPropertyName("_type")] string Type,
    int Version,
    string Expires,
    IReadOnlyDictionary<string, TufTargetFile> Targets);

/// <summary>Targets metadata envelope.</summary>
public sealed record TufTargetsMetadata(
    [property: JsonPropertyName("signed")] TufTargetsSigned SignedPayload,
    IReadOnlyList<TufSignature> Signatures);

/// <summary>ECDSA signature over canonical JSON of the signed metadata portion.</summary>
public sealed record TufSignature(
    [property: JsonPropertyName("keyid")] string KeyId,
    [property: JsonPropertyName("sig")] string Sig);

/// <summary>Thrown when TUF metadata verification fails.</summary>
public sealed class TufVerificationException : Exception
{
    /// <summary>Creates a verification exception.</summary>
    public TufVerificationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a verification exception with an inner cause.</summary>
    public TufVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
