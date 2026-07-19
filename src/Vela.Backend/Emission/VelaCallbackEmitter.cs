using System.Globalization;

namespace Vela.Backend.Emission;

/// <summary>Helpers for lowering Vela <c>Fn</c> values and lambdas to AOT-safe C# delegates.</summary>
public static class VelaCallbackEmitter
{
    /// <summary>Returns the C# delegate type for a Vela function signature <c>Fn&lt;P..., R&gt;</c>.</summary>
    public static string CSharpDelegateType(VelaType functionType)
    {
        ArgumentNullException.ThrowIfNull(functionType);
        if (!string.Equals(functionType.Name, "Fn", StringComparison.Ordinal) || functionType.TypeArguments.Count == 0)
        {
            return "Delegate";
        }

        var parameters = functionType.TypeArguments.Take(functionType.TypeArguments.Count - 1).ToArray();
        var returnType = functionType.TypeArguments[^1];
        if (returnType.IsSameAs(VelaType.Unit))
        {
            return parameters.Length switch
            {
                0 => "Action",
                1 => $"Action<{CSharpPrimitive(parameters[0])}>",
                2 => $"Action<{CSharpPrimitive(parameters[0])}, {CSharpPrimitive(parameters[1])}>",
                _ => "Delegate"
            };
        }

        var result = CSharpPrimitive(returnType);
        return parameters.Length switch
        {
            0 => $"Func<{result}>",
            1 => $"Func<{CSharpPrimitive(parameters[0])}, {result}>",
            2 => $"Func<{CSharpPrimitive(parameters[0])}, {CSharpPrimitive(parameters[1])}, {result}>",
            _ => "Delegate"
        };
    }

    /// <summary>Returns whether the function signature can be lowered to a typed Action/Func.</summary>
    public static bool IsSupportedSignature(VelaType functionType)
    {
        if (!string.Equals(functionType.Name, "Fn", StringComparison.Ordinal) || functionType.TypeArguments.Count == 0)
        {
            return false;
        }

        var parameters = functionType.TypeArguments.Take(functionType.TypeArguments.Count - 1).ToArray();
        var returnType = functionType.TypeArguments[^1];
        if (parameters.Length > 2)
        {
            return false;
        }

        if (!IsCallbackPrimitive(returnType) && !returnType.IsSameAs(VelaType.Unit))
        {
            return false;
        }

        return parameters.All(IsCallbackPrimitive);
    }

    /// <summary>Allocates a stable synthetic callback name.</summary>
    public static string NextCallbackName(ref int identifier) =>
        "__velaCb" + identifier++.ToString(CultureInfo.InvariantCulture);

    private static bool IsCallbackPrimitive(VelaType type) =>
        type.Name is "Bool" or "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal" or "Text" or "Unit";

    private static string CSharpPrimitive(VelaType type) => type.Name switch
    {
        "Bool" => "bool",
        "Int" => "int",
        "UInt" => "uint",
        "Long" => "long",
        "Float" => "float",
        "Double" => "double",
        "Decimal" => "decimal",
        "Text" => "string",
        "Unit" => "void",
        _ => "object"
    };
}
