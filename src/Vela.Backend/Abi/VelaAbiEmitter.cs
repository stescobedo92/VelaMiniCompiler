using System.Globalization;

namespace Vela.Backend.Abi;

/// <summary>ABI v2 export/import lowering helpers (status + out-buffer protocol).</summary>
public static class VelaAbiEmitter
{
    /// <summary>Returns whether <paramref name="returnType"/> uses the ABI v2 status/out-result protocol.</summary>
    public static bool UsesStatusOutProtocol(VelaType returnType) =>
        returnType.IsSameAs(VelaType.Unit)
        || returnType.IsSameAs(VelaType.Text)
        || returnType.IsSameAs(VelaType.Decimal);

    /// <summary>Formats the C# export signature return type for ABI v2.</summary>
    public static string ExportReturnCSharpType(VelaType returnType) =>
        UsesStatusOutProtocol(returnType) ? "int" : FfiScalarCSharpType(returnType);

    /// <summary>Formats additional out-parameter clauses for ABI v2 exports.</summary>
    public static string ExportOutParameterClause(VelaType returnType) => returnType.Name switch
    {
        "Text" => ", out VelaAbiBuffer result",
        "Decimal" => ", out VelaDecimal result",
        _ => string.Empty
    };

    /// <summary>Maps a runtime exception to a compact ABI status code without allocating.</summary>
    public static string StatusFromCatchArm() => """
        catch (ArgumentException)
                {
                    return (int)VelaAbiStatus.InvalidArgument;
                }
                catch (DecoderFallbackException)
                {
                    return (int)VelaAbiStatus.InvalidUtf8;
                }
                catch (OperationCanceledException)
                {
                    return (int)VelaAbiStatus.Cancelled;
                }
                catch (TimeoutException)
                {
                    return (int)VelaAbiStatus.TimedOut;
                }
                catch
                {
                    return (int)VelaAbiStatus.InternalError;
                }
        """;

    private static string FfiScalarCSharpType(VelaType type) => type.Name switch
    {
        "Bool" => "byte",
        "Int" => "int",
        "UInt" => "uint",
        "Long" => "long",
        "Float" => "float",
        "Double" => "double",
        _ => "nint"
    };

    /// <summary>Formats a C export prototype for ABI v2 headers.</summary>
    public static string FormatCPrototype(string symbol, IReadOnlyList<string> parameters, string returnType, int abiVersion)
    {
        if (abiVersion < 2 || returnType is not ("Text" or "Decimal" or "Unit"))
        {
            var cReturn = ToCType(returnType);
            var cParams = parameters.Count == 0
                ? "void"
                : string.Join(", ", parameters.Select((type, index) => $"{ToCType(type)} arg{index.ToString(CultureInfo.InvariantCulture)}"));
            return $"{cReturn} {symbol}({cParams});";
        }

        var inputs = parameters.Select((type, index) => $"{ToCType(type)} arg{index.ToString(CultureInfo.InvariantCulture)}").ToList();
        if (returnType == "Text")
        {
            inputs.Add("vela_buffer* result");
        }
        else if (returnType == "Decimal")
        {
            inputs.Add("vela_decimal* result");
        }

        var joined = inputs.Count == 0 ? "void" : string.Join(", ", inputs);
        return $"vela_status {symbol}({joined});";
    }

    private static string ToCType(string velaType) => velaType switch
    {
        "Bool" => "uint8_t",
        "Int" => "int32_t",
        "UInt" => "uint32_t",
        "Long" => "int64_t",
        "Float" => "float",
        "Double" => "double",
        "Decimal" => "vela_decimal",
        "Text" => "vela_text",
        "Unit" => "void",
        _ => "void*"
    };
}
