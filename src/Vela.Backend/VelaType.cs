namespace Vela.Backend;

/// <summary>Represents a Vela type after names have been resolved.</summary>
public sealed class VelaType
{
    public static readonly VelaType Unknown = new("<unknown>");
    public static readonly VelaType WholeNumber = new("Int");
    public static readonly VelaType FloatingPoint = new("Float");
    public static readonly VelaType Bool = new("Bool");
    public static readonly VelaType Text = new("Text");
    public static readonly VelaType Unit = new("Unit");
    public static readonly VelaType Nil = new("Nil");

    public VelaType(string name, IReadOnlyList<VelaType>? typeArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        TypeArguments = typeArguments ?? [];
    }

    public string Name { get; }

    public IReadOnlyList<VelaType> TypeArguments { get; }

    public bool IsUnknown => Name == Unknown.Name;

    public bool IsNumeric => Name is "Int" or "Float";

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
