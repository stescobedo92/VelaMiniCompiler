using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Vela.Backend.Capabilities;

/// <summary>Loads and validates the allowlisted SDK capability catalog.</summary>
public sealed class VelaCapabilityCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false
    };

    // Development SDK key; production releases pin a rotating ECDSA key in eng/capabilities.
    private static readonly byte[] DevelopmentHmacKey = Encoding.UTF8.GetBytes("vela-sdk-capability-dev-key-v1");

    private readonly Dictionary<string, VelaCapability> _capabilities;

    private VelaCapabilityCatalog(IReadOnlyList<VelaCapability> capabilities)
    {
        _capabilities = capabilities.ToDictionary(static item => item.Id, StringComparer.Ordinal);
    }

    /// <summary>Loads a catalog from disk and verifies its content hash + HMAC signature.</summary>
    public static VelaCapabilityCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new VelaCapabilityException($"Capability catalog '{path}' was not found.");
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<VelaCapabilityCatalogDocument>(json, JsonOptions)
            ?? throw new VelaCapabilityException($"Capability catalog '{path}' is empty.");

        if (document.SchemaVersion != 1)
        {
            throw new VelaCapabilityException($"Unsupported capability catalog schema version {document.SchemaVersion}.");
        }

        ValidateCapabilities(document.Capabilities);
        var canonical = Canonicalize(document.Capabilities);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!string.Equals(hash, document.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new VelaCapabilityException("Capability catalog SHA-256 does not match the declared digest.");
        }

        if (!string.Equals(document.SigningKeyId, "vela-dev-hmac-v1", StringComparison.Ordinal))
        {
            throw new VelaCapabilityException($"Unknown capability signing key '{document.SigningKeyId}'.");
        }

        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(DevelopmentHmacKey, Encoding.UTF8.GetBytes(hash))).ToLowerInvariant();
        if (!string.Equals(expectedSignature, document.Signature, StringComparison.OrdinalIgnoreCase))
        {
            throw new VelaCapabilityException("Capability catalog signature is invalid.");
        }

        return new VelaCapabilityCatalog(document.Capabilities);
    }

    /// <summary>Resolves the requested capability identifiers in declaration order.</summary>
    public IReadOnlyList<VelaCapability> Resolve(IEnumerable<string> capabilityIds)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        var resolved = new List<VelaCapability>();
        foreach (var id in capabilityIds)
        {
            if (!_capabilities.TryGetValue(id, out var capability))
            {
                throw new VelaCapabilityException($"Unknown SDK capability '{id}'.");
            }

            resolved.Add(capability);
        }

        return resolved;
    }

    /// <summary>Computes the development HMAC signature for a catalog JSON body (capabilities only).</summary>
    public static (string Sha256, string Signature) Sign(IReadOnlyList<VelaCapability> capabilities)
    {
        var canonical = Canonicalize(capabilities);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var signature = Convert.ToHexString(HMACSHA256.HashData(DevelopmentHmacKey, Encoding.UTF8.GetBytes(hash))).ToLowerInvariant();
        return (hash, signature);
    }

    private static void ValidateCapabilities(IReadOnlyList<VelaCapability> capabilities)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.Id) || !seen.Add(capability.Id))
            {
                throw new VelaCapabilityException("Capability catalog contains a missing or duplicate capability id.");
            }

            if (capability.ProjectSdk is not null &&
                (capability.ProjectSdk.Contains("..", StringComparison.Ordinal)
                 || capability.ProjectSdk.Contains('\\', StringComparison.Ordinal)
                 || capability.ProjectSdk.Contains('/', StringComparison.Ordinal)
                 || capability.ProjectSdk.Contains('<', StringComparison.Ordinal)))
            {
                throw new VelaCapabilityException($"Capability '{capability.Id}' uses a disallowed ProjectSdk path.");
            }

            foreach (var framework in capability.FrameworkReferences)
            {
                RejectMetacharacters(capability.Id, framework);
            }

            foreach (var package in capability.PackageReferences)
            {
                RejectMetacharacters(capability.Id, package.Id);
                RejectMetacharacters(capability.Id, package.Version);
            }
        }
    }

    private static void RejectMetacharacters(string capabilityId, string value)
    {
        if (value.IndexOfAny(['<', '>', '&', '"', '\'']) >= 0)
        {
            throw new VelaCapabilityException($"Capability '{capabilityId}' contains XML metacharacters.");
        }
    }

    private static string Canonicalize(IReadOnlyList<VelaCapability> capabilities)
    {
        var ordered = capabilities
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item =>
                $"{item.Id}|{item.ProjectSdk ?? ""}|{string.Join(",", item.FrameworkReferences)}|{string.Join(",", item.PackageReferences.Select(static package => $"{package.Id}@{package.Version}"))}|{string.Join(",", item.SupportedTargets)}");
        return string.Join("\n", ordered);
    }
}
