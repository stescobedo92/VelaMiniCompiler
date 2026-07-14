using System.Runtime.InteropServices;

namespace Vela.Backend;

/// <summary>Represents a runtime target selected for publishing a Vela application.</summary>
public sealed record BuildTarget(string RuntimeIdentifier, bool IsHostTarget);

/// <summary>Resolves Vela build targets without parsing runtime identifier components.</summary>
public static class BuildTargetResolver
{
    /// <summary>Gets the token that requests the runtime target of the current host.</summary>
    public const string Auto = "auto";

    /// <summary>Resolves an explicit runtime identifier or the current host target.</summary>
    public static BuildTarget Resolve(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return new BuildTarget(RuntimeInformation.RuntimeIdentifier, true);
        }

        return new BuildTarget(target.Trim(), false);
    }
}
