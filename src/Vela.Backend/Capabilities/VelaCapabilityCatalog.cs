using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Backend.Capabilities;

/// <summary>Loads and validates the allowlisted SDK capability catalog.</summary>
public sealed class VelaCapabilityCatalog
{
    public const string ProductionSigningKeyId = "vela-ecdsa-p256-v1";

    /// <summary>SubjectPublicKeyInfo (SPKI) for the production ECDSA P-256 catalog signing key.</summary>
    private static readonly byte[] ProductionPublicKeySpki = Convert.FromBase64String(
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFhOsdpyxOZrT+AQU6qDT85zYLm6xdJy4e9ju1X4HCJ8fqGEVVraxbvVncrkogZmT2LQftrsicKKihD0wGFSjpg==");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false
    };

    private readonly Dictionary<string, VelaCapability> _capabilities;

    private VelaCapabilityCatalog(IReadOnlyList<VelaCapability> capabilities)
    {
        _capabilities = capabilities.ToDictionary(static item => item.Id, StringComparer.Ordinal);
    }

    /// <summary>Loads the SDK-shipped catalog from <c>eng/capabilities</c> relative to the runtime project.</summary>
    public static VelaCapabilityCatalog LoadDefault(string runtimeProjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeProjectPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(runtimeProjectPath))
            ?? throw new VelaCapabilityException("Unable to resolve the runtime project directory.");
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "eng", "capabilities", "vela-capabilities.json");
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }

        throw new VelaCapabilityException("SDK capability catalog 'eng/capabilities/vela-capabilities.json' was not found.");
    }

    /// <summary>Loads a catalog from disk and verifies its content hash + ECDSA P-256 signature.</summary>
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

        if (!string.Equals(document.SigningKeyId, ProductionSigningKeyId, StringComparison.Ordinal))
        {
            throw new VelaCapabilityException($"Unknown capability signing key '{document.SigningKeyId}'.");
        }

        var signatureBytes = Convert.FromHexString(document.Signature);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(ProductionPublicKeySpki, out _);
        if (!ecdsa.VerifyData(Encoding.UTF8.GetBytes(hash), signatureBytes, HashAlgorithmName.SHA256))
        {
            throw new VelaCapabilityException("Capability catalog ECDSA signature is invalid.");
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

    /// <summary>Maps compilation adapter flags onto catalog capability identifiers.</summary>
    public static IReadOnlyList<string> CapabilitiesFor(VelaCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var ids = new List<string>();
        if (compilation.RequiresHttp || compilation.RequiresGrpc)
        {
            ids.Add("aspnet-server");
        }

        if (compilation.RequiresGui)
        {
            ids.Add("avalonia-ui");
        }

        if (compilation.RequiresSqlite)
        {
            ids.Add("sqlite");
        }

        if (compilation.RequiresPostgres)
        {
            ids.Add("postgres");
        }

        return ids;
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

    internal static string Canonicalize(IReadOnlyList<VelaCapability> capabilities)
    {
        var ordered = capabilities
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item =>
                $"{item.Id}|{item.ProjectSdk ?? ""}|{string.Join(",", item.FrameworkReferences)}|{string.Join(",", item.PackageReferences.Select(static package => $"{package.Id}@{package.Version}"))}|{string.Join(",", item.SupportedTargets)}");
        return string.Join("\n", ordered);
    }
}
