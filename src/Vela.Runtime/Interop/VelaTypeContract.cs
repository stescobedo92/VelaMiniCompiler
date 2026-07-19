using System.Diagnostics.CodeAnalysis;

namespace Vela.Runtime.Interop;

/// <summary>Stable type-contract identifiers used by ABI v2 handles.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "ABI contract names mirror wire type identifiers.")]
public static class VelaTypeContract
{
    /// <summary>Contract for UTF-8 text values.</summary>
    public const ulong Text = 0x54657874_00000001;

    /// <summary>Contract for decimal wire values.</summary>
    public const ulong Decimal = 0x44656369_00000002;

    /// <summary>Contract for opaque object handles.</summary>
    public const ulong Object = 0x4F626A65_00000003;

    /// <summary>Contract for asynchronous task handles.</summary>
    public const ulong Task = 0x5461736B_00000004;
}
