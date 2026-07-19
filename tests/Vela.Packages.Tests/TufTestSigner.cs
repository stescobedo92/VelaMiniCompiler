using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vela.Packages.Tuf;

namespace Vela.Packages.Tests;

internal static class TufTestSigner
{
    public const string TestKeyId = "dogfood-test-key";

    public static (ECDsa PrivateKey, string PublicKeyBase64) CreateTestKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return (key, publicKey);
    }

    public static (string RootJson, string TargetsJson) SignTargets(
        ECDsa privateKey,
        string publicKeyBase64,
        IReadOnlyDictionary<string, TufTargetFile> targets,
        DateTimeOffset? expires = null)
    {
        expires ??= DateTimeOffset.UtcNow.AddYears(10);
        var expiresText = expires.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var rootSigned = new Dictionary<string, object?>
        {
            ["_type"] = "root",
            ["spec_version"] = "1.0",
            ["version"] = 1,
            ["expires"] = expiresText,
            ["keys"] = new Dictionary<string, object?>
            {
                [TestKeyId] = new Dictionary<string, object?>
                {
                    ["keytype"] = "ecdsa-sha2-nistp256",
                    ["keyval"] = new Dictionary<string, object?> { ["public"] = publicKeyBase64 },
                    ["scheme"] = "ecdsa-sha2-nistp256"
                }
            },
            ["roles"] = new Dictionary<string, object?>
            {
                ["root"] = new Dictionary<string, object?> { ["keyids"] = new[] { TestKeyId }, ["threshold"] = 1 },
                ["targets"] = new Dictionary<string, object?> { ["keyids"] = new[] { TestKeyId }, ["threshold"] = 1 }
            }
        };

        var targetsSigned = new Dictionary<string, object?>
        {
            ["_type"] = "targets",
            ["spec_version"] = "1.0",
            ["version"] = 1,
            ["expires"] = expiresText,
            ["targets"] = targets.ToDictionary(
                static pair => pair.Key,
                static pair => (object?)new Dictionary<string, object?>
                {
                    ["length"] = pair.Value.Length,
                    ["hashes"] = new Dictionary<string, object?> { ["sha256"] = pair.Value.Hashes.Sha256 }
                })
        };

        return (SignEnvelope(privateKey, rootSigned), SignEnvelope(privateKey, targetsSigned));
    }

    private static string SignEnvelope(ECDsa privateKey, Dictionary<string, object?> signed)
    {
        var signedJson = JsonSerializer.Serialize(signed);
        var signedBytes = Encoding.UTF8.GetBytes(signedJson);
        var envelopeBytes = Encoding.UTF8.GetBytes($"{{\"signed\":{signedJson}}}");
        var canonical = TufCanonicalJson.EncodeSignedPortion(envelopeBytes);
        var signature = Convert.ToBase64String(privateKey.SignData(canonical, HashAlgorithmName.SHA256));
        var envelope = new Dictionary<string, object?>
        {
            ["signed"] = signed,
            ["signatures"] = new[]
            {
                new Dictionary<string, object?> { ["keyid"] = TestKeyId, ["sig"] = signature }
            }
        };

        return JsonSerializer.Serialize(envelope);
    }
}
