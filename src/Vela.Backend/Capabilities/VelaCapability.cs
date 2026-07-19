namespace Vela.Backend.Capabilities;

/// <summary>Thrown when the SDK capability catalog is invalid or a requested capability is unknown.</summary>
public sealed class VelaCapabilityException(string message) : Exception(message);

/// <summary>One managed NuGet package allowed by a capability.</summary>
public sealed record VelaManagedPackage(string Id, string Version);

/// <summary>Immutable allowlisted SDK capability used when generating projects.</summary>
public sealed record VelaCapability(
    string Id,
    string? ProjectSdk,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<VelaManagedPackage> PackageReferences,
    IReadOnlyList<string> SupportedTargets);

/// <summary>Signed catalog document shipped with the SDK.</summary>
public sealed record VelaCapabilityCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<VelaCapability> Capabilities,
    string Sha256,
    string SigningKeyId,
    string Signature);
