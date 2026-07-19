using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Packages.Tuf;

/// <summary>Verifies simplified single-role TUF root and targets metadata.</summary>
public static class TufVerifier
{
    private const string ExpectedKeyType = "ecdsa-sha2-nistp256";

    /// <summary>Validates <paramref name="targetsJson"/> against <paramref name="rootJson"/> and returns the target map.</summary>
    public static IReadOnlyDictionary<string, TufTargetFile> VerifyTargets(string rootJson, string targetsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetsJson);

        var rootBytes = Encoding.UTF8.GetBytes(rootJson);
        var targetsBytes = Encoding.UTF8.GetBytes(targetsJson);
        var root = JsonSerializer.Deserialize(rootBytes, TufJsonContext.Default.TufRootMetadata)
            ?? throw new TufVerificationException("Root metadata is empty.");
        var targets = JsonSerializer.Deserialize(targetsBytes, TufJsonContext.Default.TufTargetsMetadata)
            ?? throw new TufVerificationException("Targets metadata is empty.");

        ValidateSignedEnvelope(
            root.Signatures,
            rootBytes,
            "root",
            root.SignedPayload.Roles,
            root.SignedPayload.Keys);
        ValidateSignedEnvelope(
            targets.Signatures,
            targetsBytes,
            "targets",
            root.SignedPayload.Roles,
            root.SignedPayload.Keys,
            roleName: "targets");

        if (targets.SignedPayload.Targets.Count == 0)
        {
            throw new TufVerificationException("Targets metadata does not contain any targets.");
        }

        return targets.SignedPayload.Targets;
    }

    /// <summary>Computes the lowercase hex SHA-256 digest of a file.</summary>
    public static string ComputeSha256Hex(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Builds the registry-relative archive path checked against targets metadata.</summary>
    public static string BuildArchiveTargetPath(string packageName, string version) =>
        $"{packageName}/{version}/package{VelaDependencyArchive.Extension}";

    /// <summary>Verifies an archive against a TUF target entry.</summary>
    public static void VerifyArchive(string archivePath, TufTargetFile target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(target);

        if (!File.Exists(archivePath))
        {
            throw new TufVerificationException($"Archive '{archivePath}' was not found.");
        }

        var length = new FileInfo(archivePath).Length;
        if (length != target.Length)
        {
            throw new TufVerificationException(
                $"Archive length {length} does not match TUF metadata length {target.Length}.");
        }

        var digest = ComputeSha256Hex(archivePath);
        if (!string.Equals(digest, target.Hashes.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TufVerificationException("Archive SHA-256 does not match signed TUF targets metadata.");
        }
    }

    private static void ValidateSignedEnvelope(
        IReadOnlyList<TufSignature> signatures,
        ReadOnlySpan<byte> metadataJson,
        string expectedType,
        IReadOnlyDictionary<string, TufRoleDefinition> roles,
        IReadOnlyDictionary<string, TufPublicKey> keys,
        string? roleName = null)
    {
        using var document = JsonDocument.Parse(metadataJson.ToArray());
        if (!document.RootElement.TryGetProperty("signed", out var signed))
        {
            throw new TufVerificationException("TUF metadata is missing the signed portion.");
        }

        var type = signed.TryGetProperty("_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (!string.Equals(type, expectedType, StringComparison.Ordinal))
        {
            throw new TufVerificationException($"Expected TUF metadata type '{expectedType}', got '{type}'.");
        }

        if (!signed.TryGetProperty("expires", out var expiresElement)
            || expiresElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(expiresElement.GetString(), out var expires))
        {
            throw new TufVerificationException("TUF metadata expiry is invalid.");
        }

        if (expires <= DateTimeOffset.UtcNow)
        {
            throw new TufVerificationException("TUF metadata has expired.");
        }

        roleName ??= expectedType;
        if (!roles.TryGetValue(roleName, out var role))
        {
            throw new TufVerificationException($"Root metadata does not define role '{roleName}'.");
        }

        if (role.Threshold <= 0)
        {
            throw new TufVerificationException($"Role '{roleName}' has an invalid signature threshold.");
        }

        var canonical = TufCanonicalJson.EncodeSignedPortion(metadataJson);
        var validSignatures = 0;
        var seenKeyIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var signature in signatures)
        {
            if (!role.KeyIds.Contains(signature.KeyId, StringComparer.Ordinal))
            {
                continue;
            }

            if (!seenKeyIds.Add(signature.KeyId))
            {
                continue;
            }

            if (!keys.TryGetValue(signature.KeyId, out var key))
            {
                throw new TufVerificationException($"Root metadata does not contain key '{signature.KeyId}'.");
            }

            if (!string.Equals(key.KeyType, ExpectedKeyType, StringComparison.Ordinal))
            {
                throw new TufVerificationException($"Unsupported TUF key type '{key.KeyType}'.");
            }

            if (VerifySignature(key.KeyVal.Public, signature.Sig, canonical))
            {
                validSignatures++;
            }
        }

        if (validSignatures < role.Threshold)
        {
            throw new TufVerificationException(
                $"TUF metadata for role '{roleName}' has insufficient valid signatures.");
        }
    }

    private static bool VerifySignature(string publicKeyBase64, string signatureBase64, ReadOnlySpan<byte> payload)
    {
        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
            return ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch (FormatException ex)
        {
            throw new TufVerificationException("TUF key or signature encoding is invalid.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new TufVerificationException("TUF signature verification failed.", ex);
        }
    }
}
