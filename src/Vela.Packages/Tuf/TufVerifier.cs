using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Packages.Tuf;

/// <summary>Verifies simplified single-role TUF root and targets metadata.</summary>
/// <remarks>
/// Low-level verification assumes the supplied root is trusted. VelaRestoreClient
/// provisions that trust out of band and persists root rotation and rollback state.
/// This remains a root/targets subset; timestamp, snapshot and delegation roles are not implemented.
/// </remarks>
public static class TufVerifier
{
    private const string ExpectedKeyType = "ecdsa-sha2-nistp256";
    private static readonly string[] ExpiryFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];

    /// <summary>Validates <paramref name="targetsJson"/> against <paramref name="rootJson"/> and returns the target map.</summary>
    public static IReadOnlyDictionary<string, TufTargetFile> VerifyTargets(string rootJson, string targetsJson)
        => VerifyTargetsMetadata(rootJson, targetsJson, DateTimeOffset.UtcNow).SignedPayload.Targets;

    internal static TufTargetsMetadata VerifyTargetsMetadata(string rootJson, string targetsJson, DateTimeOffset updateTime)
    {
        if (string.IsNullOrWhiteSpace(rootJson) || string.IsNullOrWhiteSpace(targetsJson))
        {
            throw new TufVerificationException("Root and targets metadata must not be empty.");
        }

        var rootBytes = Encoding.UTF8.GetBytes(rootJson);
        var targetsBytes = Encoding.UTF8.GetBytes(targetsJson);
        TufRootMetadata root;
        TufTargetsMetadata targets;
        try
        {
            ValidateJson(rootBytes);
            ValidateJson(targetsBytes);
            root = JsonSerializer.Deserialize(rootBytes, TufJsonContext.Default.TufRootMetadata)
                ?? throw new TufVerificationException("Root metadata is empty.");
            targets = JsonSerializer.Deserialize(targetsBytes, TufJsonContext.Default.TufTargetsMetadata)
                ?? throw new TufVerificationException("Targets metadata is empty.");
        }
        catch (JsonException ex)
        {
            throw new TufVerificationException("TUF metadata contains invalid JSON or missing or null required members.", ex);
        }

        ValidateRootCollections(root);
        ValidateVersion(root.SignedPayload.Version);
        ValidateVersion(targets.SignedPayload.Version);
        ValidateSignatures(targets.Signatures);
        foreach (var target in targets.SignedPayload.Targets.Values)
        {
            ValidateTarget(target);
        }

        ValidateSignedEnvelope(
            root.Signatures,
            rootBytes,
            "root",
            root.SignedPayload.Roles,
            root.SignedPayload.Keys,
            updateTime: updateTime);
        ValidateSignedEnvelope(
            targets.Signatures,
            targetsBytes,
            "targets",
            root.SignedPayload.Roles,
            root.SignedPayload.Keys,
            roleName: "targets",
            updateTime: updateTime);

        if (targets.SignedPayload.Targets.Count == 0)
        {
            throw new TufVerificationException("Targets metadata does not contain any targets.");
        }

        return targets;
    }

    internal static TufRootMetadata VerifyRoot(string rootJson, DateTimeOffset updateTime, bool allowExpired = false)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(rootJson);
            ValidateJson(bytes);
            var root = JsonSerializer.Deserialize(bytes, TufJsonContext.Default.TufRootMetadata)
                ?? throw new TufVerificationException("Root metadata is empty.");
            ValidateRootCollections(root);
            ValidateVersion(root.SignedPayload.Version);
            ValidateSignedEnvelope(root.Signatures, bytes, "root", root.SignedPayload.Roles,
                root.SignedPayload.Keys, allowExpired: allowExpired, updateTime: updateTime);
            return root;
        }
        catch (JsonException ex)
        {
            throw new TufVerificationException("TUF root contains invalid JSON or missing or null required members.", ex);
        }
    }

    internal static void VerifyRootRotation(string trustedRootJson, string nextRootJson, DateTimeOffset updateTime)
    {
        var oldRoot = VerifyRoot(trustedRootJson, updateTime, allowExpired: true);
        var nextRoot = VerifyRoot(nextRootJson, updateTime, allowExpired: true);
        if ((long)nextRoot.SignedPayload.Version != (long)oldRoot.SignedPayload.Version + 1)
        {
            throw new TufVerificationException("TUF root rotation must increment the version by exactly one.");
        }

        ValidateSignedEnvelope(nextRoot.Signatures, Encoding.UTF8.GetBytes(nextRootJson), "root",
            oldRoot.SignedPayload.Roles, oldRoot.SignedPayload.Keys, allowExpired: true, updateTime: updateTime);
    }

    internal static string SignedDigest(string metadataJson) =>
        Convert.ToHexString(SHA256.HashData(TufCanonicalJson.EncodeSignedPortion(Encoding.UTF8.GetBytes(metadataJson))));

    private static void ValidateVersion(int version)
    {
        if (version < 1)
        {
            throw new TufVerificationException("TUF metadata version must be positive.");
        }
    }

    private static void ValidateJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
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
        ValidateTarget(target);

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

    private static void ValidateRootCollections(TufRootMetadata root)
    {
        ValidateSignatures(root.Signatures);
        foreach (var key in root.SignedPayload.Keys.Values)
        {
            if (key is null)
            {
                throw new TufVerificationException("Root metadata contains a null key.");
            }
        }

        foreach (var role in root.SignedPayload.Roles.Values)
        {
            if (role is null || role.KeyIds.Any(static keyId => keyId is null))
            {
                throw new TufVerificationException("Root metadata contains a null role or key id.");
            }
        }
    }

    private static void ValidateSignatures(IReadOnlyList<TufSignature> signatures)
    {
        if (signatures.Any(static signature => signature is null))
        {
            throw new TufVerificationException("TUF metadata contains a null signature.");
        }
    }

    private static void ValidateTarget(TufTargetFile? target)
    {
        if (target is null || target.Hashes is null || string.IsNullOrWhiteSpace(target.Hashes.Sha256))
        {
            throw new TufVerificationException("TUF target metadata is missing the target or its SHA-256 hash.");
        }

        if (target.Length < 0)
        {
            throw new TufVerificationException("TUF target metadata has a negative archive length.");
        }
    }

    private static void ValidateSignedEnvelope(
        IReadOnlyList<TufSignature> signatures,
        ReadOnlySpan<byte> metadataJson,
        string expectedType,
        IReadOnlyDictionary<string, TufRoleDefinition> roles,
        IReadOnlyDictionary<string, TufPublicKey> keys,
        string? roleName = null,
        bool allowExpired = false,
        DateTimeOffset? updateTime = null)
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
            || !DateTimeOffset.TryParseExact(expiresElement.GetString(), ExpiryFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
        {
            throw new TufVerificationException("TUF metadata expiry is invalid.");
        }

        if (!allowExpired && expires <= (updateTime ?? DateTimeOffset.UtcNow))
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
