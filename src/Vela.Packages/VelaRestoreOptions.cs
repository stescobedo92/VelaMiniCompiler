namespace Vela.Packages;

/// <summary>Explicit trust policy for a registry restore operation.</summary>
public sealed record VelaRestoreOptions
{
    /// <summary>Gets the path to an initial root obtained through a trusted, out-of-band channel.</summary>
    public string? TrustedRootPath { get; init; }

    /// <summary>Gets whether registries without TUF metadata may be used before trust has been established.</summary>
    public bool AllowUnsigned { get; init; }
}
