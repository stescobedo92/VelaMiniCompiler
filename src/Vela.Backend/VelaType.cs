namespace Vela.Backend;

/// <summary>Represents a Vela type after names have been resolved.</summary>
public sealed class VelaType
{
#pragma warning disable CA1720 // The field names intentionally match the Vela language surface.
    public static readonly VelaType Unknown = new("<unknown>");
    public static readonly VelaType Int = new("Int");
    public static readonly VelaType UInt = new("UInt");
    public static readonly VelaType Long = new("Long");
    public static readonly VelaType Float = new("Float");
    public static readonly VelaType Double = new("Double");
    public static readonly VelaType Decimal = new("Decimal");
    public static readonly VelaType Bool = new("Bool");
    public static readonly VelaType Text = new("Text");
    public static readonly VelaType Any = new("Any");
    public static readonly VelaType Unit = new("Unit");
    public static readonly VelaType Nil = new("Nil");
#pragma warning restore CA1720

    /// <summary>Compatibility alias for Vela's default signed whole number type.</summary>
    public static VelaType WholeNumber => Int;

    /// <summary>Compatibility alias for Vela's default floating-point literal type.</summary>
    public static VelaType FloatingPoint => Double;

    public VelaType(string name, IReadOnlyList<VelaType>? typeArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        TypeArguments = typeArguments ?? [];
    }

    public string Name { get; }

    public IReadOnlyList<VelaType> TypeArguments { get; }

    public bool IsUnknown => Name == Unknown.Name;

    public bool IsNumeric => Name is "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal";

    public bool IsOptional => Name == "Option" && TypeArguments.Count == 1;

    public bool IsSameAs(VelaType other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal) || TypeArguments.Count != other.TypeArguments.Count)
        {
            return false;
        }

        for (var index = 0; index < TypeArguments.Count; index++)
        {
            if (!TypeArguments[index].IsSameAs(other.TypeArguments[index]))
            {
                return false;
            }
        }

        return true;
    }

    public VelaType Substitute(IReadOnlyDictionary<string, VelaType> substitutions)
    {
        if (substitutions.TryGetValue(Name, out var replacement) && TypeArguments.Count == 0)
        {
            return replacement;
        }

        if (TypeArguments.Count == 0)
        {
            return this;
        }

        return new VelaType(Name, TypeArguments.Select(type => type.Substitute(substitutions)).ToArray());
    }

    public override string ToString() => TypeArguments.Count == 0
        ? Name
        : $"{Name}<{string.Join(", ", TypeArguments.Select(static type => type.ToString()))}>";
}
