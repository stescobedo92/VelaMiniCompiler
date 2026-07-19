using System.Globalization;
using System.Text;
using Vela.Backend.Abi;
using Vela.Backend.Emission;
using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Backend;

internal sealed partial class CSharpEmitter
{
    private void EmitNativeExports(CompilationUnitSyntax root)
    {
        var ffiFunctions = root.Members.OfType<FunctionDeclarationSyntax>()
            .Where(static function => function.FfiKeyword is not null)
            .ToArray();
        foreach (var function in ffiFunctions)
        {
            if (function.PublicKeyword is null)
            {
                Report("VEL3010", function.FfiKeyword!.Span, "An FFI function must be declared public.", "Use 'public ffi fn'.");
            }
        }

        _writer.WriteLine("public static class VelaExports");
        _writer.WriteLine("{");
        _writer.Indent();
        foreach (var function in ffiFunctions)
        {
            EmitNativeExport(function);
        }

        // Shared ABI v2 lifecycle symbol — only once per library, zero cost when unused by callers.
        _writer.WriteLine("[UnmanagedCallersOnly(EntryPoint = \"vela_buffer_release\", CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
        _writer.WriteLine("public static void BufferRelease(VelaAbiBuffer value) => value.Free();");
        _writer.WriteLine();

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitNativeExport(FunctionDeclarationSyntax function)
    {
        if (function.AsyncKeyword is not null)
        {
            Report("VEL3019", function.AsyncKeyword.Span, "An async function cannot be exported through the native FFI.", "Expose a synchronous ABI-safe wrapper instead.");
            return;
        }

        if (function.GenericParameters.Count != 0)
        {
            Report("VEL3010", function.Identifier.Span, "FFI functions cannot be generic.", "Expose a concrete FFI-safe signature.");
            return;
        }

        var returnType = ResolveType(function.ReturnType, EmptyGenericNames, function.Identifier.Span, VelaType.Unit, allowVoid: true);
        var parameterTypes = function.Parameters.Select(parameter => ResolveType(parameter.Type, EmptyGenericNames, parameter.Type.Span)).ToArray();
        if (!IsFfiSafe(returnType) || parameterTypes.Any(type => !IsFfiSafe(type)))
        {
            Report("VEL3010", function.Identifier.Span, $"FFI function '{function.Identifier.Text}' uses a type that is not ABI-safe.", "Use primitive values, Text, Decimal, or Unit at the shared-library boundary.");
            return;
        }

        var symbol = $"vela_{SanitizeNativeName(_libraryPackageName)}_{SanitizeNativeName(function.Identifier.Text)}";
        _ffiExports.Add(new VelaFfiExport(function.Identifier.Text, symbol, parameterTypes.Select(static type => type.ToString()).ToArray(), returnType.ToString()));
        var parameters = string.Join(", ", function.Parameters.Select((parameter, index) =>
            $"{FfiCSharpType(parameterTypes[index])} {EscapeIdentifier(parameter.Identifier.Text)}"));
        var outClause = VelaAbiEmitter.ExportOutParameterClause(returnType);
        var exportReturn = VelaAbiEmitter.ExportReturnCSharpType(returnType);
        var arguments = function.Parameters.Select((parameter, index) => FfiIncomingCode(parameterTypes[index], EscapeIdentifier(parameter.Identifier.Text)));
        var invocation = $"Program.{EscapeIdentifier(function.Identifier.Text)}({string.Join(", ", arguments)})";
        _writer.WriteLine($"[UnmanagedCallersOnly(EntryPoint = \"{symbol}\", CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
        _writer.WriteLine($"public static {exportReturn} {EscapeIdentifier(function.Identifier.Text)}({parameters}{outClause})");
        _writer.WriteLine("{");
        _writer.Indent();
        if (!VelaAbiEmitter.UsesStatusOutProtocol(returnType))
        {
            // Hot scalar path: direct return, no status/out allocation.
            _writer.WriteLine($"return {FfiOutgoingCode(returnType, invocation)};");
            _writer.Unindent();
            _writer.WriteLine("}");
            _writer.WriteLine();
            return;
        }

        if (returnType.IsSameAs(VelaType.Text) || returnType.IsSameAs(VelaType.Decimal))
        {
            _writer.WriteLine("result = default;");
        }

        _writer.WriteLine("try");
        _writer.WriteLine("{");
        _writer.Indent();
        if (returnType.IsSameAs(VelaType.Unit))
        {
            _writer.WriteLine(invocation + ";");
            _writer.WriteLine("return (int)VelaAbiStatus.Success;");
        }
        else if (returnType.IsSameAs(VelaType.Text))
        {
            _writer.WriteLine($"result = VelaAbiBuffer.FromUtf8({invocation});");
            _writer.WriteLine("return (int)VelaAbiStatus.Success;");
        }
        else
        {
            _writer.WriteLine($"result = VelaDecimal.FromDecimal({invocation});");
            _writer.WriteLine("return (int)VelaAbiStatus.Success;");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
        _writer.WriteLine(VelaAbiEmitter.StatusFromCatchArm().TrimEnd());
        _writer.Unindent();
        _writer.WriteLine("}");
        _writer.WriteLine();
    }
    private void EmitImportedBindings()
    {
        _writer.WriteLine("internal static partial class VelaImports");
        _writer.WriteLine("{");
        _writer.Indent();
        foreach (var pair in _importsByAlias.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var exportItem in pair.Value.Manifest.Exports.OrderBy(static exportItem => exportItem.Symbol, StringComparer.Ordinal))
            {
                var returnType = ParseAbiType(exportItem.ReturnType);
                var parameterTypes = exportItem.Parameters.Select(ParseAbiType).ToArray();
                if (!IsInteropImportSafe(returnType) || parameterTypes.Any(type => !IsInteropImportSafe(type)))
                {
                    continue;
                }

                var methodName = ImportedMethodName(pair.Key, exportItem.Name);
                var abiV2 = pair.Value.Manifest.AbiVersion >= 2;
                var needsWireWrapper = RequiresWireWrapper(returnType) || parameterTypes.Any(RequiresWireWrapper)
                    || (abiV2 && VelaAbiEmitter.UsesStatusOutProtocol(returnType));
                if (!needsWireWrapper)
                {
                    var parameters = string.Join(", ", parameterTypes.Select((type, index) => $"{CSharpType(type)} value{index.ToString(CultureInfo.InvariantCulture)}"));
                    _writer.WriteLine($"[LibraryImport(\"{EscapeStringForAttribute(pair.Value.LibraryName)}\", EntryPoint = \"{EscapeStringForAttribute(exportItem.Symbol)}\")]");
                    _writer.WriteLine("[UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
                    _writer.WriteLine($"internal static partial {CSharpType(returnType)} {methodName}({parameters});");
                    _writer.WriteLine();
                    continue;
                }

                var rawName = methodName + "_Raw";
                var rawParameters = string.Join(", ", parameterTypes.Select((type, index) => $"{FfiCSharpType(type)} value{index.ToString(CultureInfo.InvariantCulture)}"));
                if (abiV2 && returnType.IsSameAs(VelaType.Text))
                {
                    rawParameters = string.IsNullOrEmpty(rawParameters) ? "out VelaAbiBuffer result" : rawParameters + ", out VelaAbiBuffer result";
                }
                else if (abiV2 && returnType.IsSameAs(VelaType.Decimal))
                {
                    rawParameters = string.IsNullOrEmpty(rawParameters) ? "out VelaDecimal result" : rawParameters + ", out VelaDecimal result";
                }

                var rawReturn = abiV2 && VelaAbiEmitter.UsesStatusOutProtocol(returnType) ? "int" : FfiCSharpType(returnType);
                _writer.WriteLine($"[LibraryImport(\"{EscapeStringForAttribute(pair.Value.LibraryName)}\", EntryPoint = \"{EscapeStringForAttribute(exportItem.Symbol)}\")]");
                _writer.WriteLine("[UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
                _writer.WriteLine($"private static partial {rawReturn} {rawName}({rawParameters});");
                _writer.WriteLine();

                var managedParameters = string.Join(", ", parameterTypes.Select((type, index) => $"{CSharpType(type)} value{index.ToString(CultureInfo.InvariantCulture)}"));
                var managedReturn = returnType.IsSameAs(VelaType.Unit) ? "void" : CSharpType(returnType);
                _writer.WriteLine($"internal static {managedReturn} {methodName}({managedParameters})");
                _writer.WriteLine("{");
                _writer.Indent();
                for (var index = 0; index < parameterTypes.Length; index++)
                {
                    if (parameterTypes[index].IsSameAs(VelaType.Text))
                    {
                        _writer.WriteLine($"var __wire{index.ToString(CultureInfo.InvariantCulture)} = VelaText.FromString(value{index.ToString(CultureInfo.InvariantCulture)});");
                    }
                    else if (parameterTypes[index].IsSameAs(VelaType.Decimal))
                    {
                        _writer.WriteLine($"var __wire{index.ToString(CultureInfo.InvariantCulture)} = VelaDecimal.FromDecimal(value{index.ToString(CultureInfo.InvariantCulture)});");
                    }
                }

                var textParams = Enumerable.Range(0, parameterTypes.Length).Where(index => parameterTypes[index].IsSameAs(VelaType.Text)).ToArray();
                if (textParams.Length > 0)
                {
                    _writer.WriteLine("try");
                    _writer.WriteLine("{");
                    _writer.Indent();
                }

                var callArgs = string.Join(", ", parameterTypes.Select((type, index) =>
                    type.IsSameAs(VelaType.Text) || type.IsSameAs(VelaType.Decimal)
                        ? $"__wire{index.ToString(CultureInfo.InvariantCulture)}"
                        : $"value{index.ToString(CultureInfo.InvariantCulture)}"));

                if (abiV2 && returnType.IsSameAs(VelaType.Text))
                {
                    var statusCall = string.IsNullOrEmpty(callArgs) ? $"{rawName}(out var __buf)" : $"{rawName}({callArgs}, out var __buf)";
                    _writer.WriteLine($"var __status = {statusCall};");
                    _writer.WriteLine("if (__status != (int)VelaAbiStatus.Success) { throw new InvalidOperationException($\"Native ABI call failed with status {__status}.\"); }");
                    _writer.WriteLine("try { return __buf.ToUtf8String(); }");
                    _writer.WriteLine("finally { __buf.Free(); }");
                }
                else if (abiV2 && returnType.IsSameAs(VelaType.Decimal))
                {
                    var statusCall = string.IsNullOrEmpty(callArgs) ? $"{rawName}(out var __dec)" : $"{rawName}({callArgs}, out var __dec)";
                    _writer.WriteLine($"var __status = {statusCall};");
                    _writer.WriteLine("if (__status != (int)VelaAbiStatus.Success) { throw new InvalidOperationException($\"Native ABI call failed with status {__status}.\"); }");
                    _writer.WriteLine("return __dec.ToDecimal();");
                }
                else if (abiV2 && returnType.IsSameAs(VelaType.Unit))
                {
                    _writer.WriteLine($"var __status = {rawName}({callArgs});");
                    _writer.WriteLine("if (__status != (int)VelaAbiStatus.Success) { throw new InvalidOperationException($\"Native ABI call failed with status {__status}.\"); }");
                }
                else if (returnType.IsSameAs(VelaType.Unit))
                {
                    _writer.WriteLine($"{rawName}({callArgs});");
                }
                else if (returnType.IsSameAs(VelaType.Text))
                {
                    _writer.WriteLine($"var __result = {rawName}({callArgs});");
                    _writer.WriteLine("try { return __result.ToManagedString(); }");
                    _writer.WriteLine("finally { VelaText.Free(__result); }");
                }
                else if (returnType.IsSameAs(VelaType.Decimal))
                {
                    _writer.WriteLine($"return {rawName}({callArgs}).ToDecimal();");
                }
                else
                {
                    var rawCall = rawName + "(" + callArgs + ")";
                    _writer.WriteLine($"return {FfiIncomingCode(returnType, rawCall)};");
                }

                if (textParams.Length > 0)
                {
                    _writer.Unindent();
                    _writer.WriteLine("}");
                    _writer.WriteLine("finally");
                    _writer.WriteLine("{");
                    _writer.Indent();
                    foreach (var index in textParams)
                    {
                        _writer.WriteLine($"VelaText.Free(__wire{index.ToString(CultureInfo.InvariantCulture)});");
                    }

                    _writer.Unindent();
                    _writer.WriteLine("}");
                }

                _writer.Unindent();
                _writer.WriteLine("}");
                _writer.WriteLine();
            }
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private static bool RequiresWireWrapper(VelaType type) => type.IsSameAs(VelaType.Text) || type.IsSameAs(VelaType.Decimal);
    private ExpressionResult EmitImportedFunctionCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, VelaLibraryImport importItem, Scope scope)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, "Native package imports do not accept generic type arguments.");
        }

        var exportItem = importItem.Manifest.Exports.FirstOrDefault(candidate => candidate.Name == member.Member.Text);
        if (exportItem is null)
        {
            Report("VEL3005", member.Member.Span, $"Package '{importItem.PackageName}' does not export function '{member.Member.Text}'.", "Use a function listed in the package ABI manifest.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        var returnType = ParseAbiType(exportItem.ReturnType);
        var parameterTypes = exportItem.Parameters.Select(ParseAbiType).ToArray();
        if (!IsInteropImportSafe(returnType) || parameterTypes.Any(type => !IsInteropImportSafe(type)))
        {
            Report("VEL3010", member.Member.Span, $"Package function '{member.Member.Text}' uses an ABI type not supported by the current native importer.", "Use Bool, Int, UInt, Long, Float, Double, Decimal, Text, or Unit at the ABI boundary.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        if (arguments.Length != parameterTypes.Length)
        {
            Report("VEL3006", call.Span, $"Package function '{member.Member.Text}' expects {parameterTypes.Length} argument(s), but received {arguments.Length}.");
        }

        var argumentCodes = arguments.Select((argument, index) =>
        {
            if (index >= parameterTypes.Length)
            {
                return argument.Code;
            }

            EnsureAssignable(parameterTypes[index], argument.Type, call.Arguments[index].Span);
            return CoerceCode(parameterTypes[index], argument, call.Arguments[index]);
        });
        return new ExpressionResult(returnType, $"VelaImports.{ImportedMethodName(member.Receiver is NameExpressionSyntax receiver ? receiver.Identifier.Text : string.Empty, exportItem.Name)}({string.Join(", ", argumentCodes)})");
    }
    private static bool IsFfiSafe(VelaType type) => type.Name is "Bool" or "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal" or "Text" or "Unit";

    private static bool IsInteropImportSafe(VelaType type) => type.Name is "Bool" or "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal" or "Text" or "Unit";

    private static VelaType ParseAbiType(string value) => value switch
    {
        "Bool" => VelaType.Bool,
        "Int" => VelaType.Int,
        "UInt" => VelaType.UInt,
        "Long" => VelaType.Long,
        "Float" => VelaType.Float,
        "Double" => VelaType.Double,
        "Decimal" => VelaType.Decimal,
        "Text" => VelaType.Text,
        "Unit" => VelaType.Unit,
        _ => VelaType.Unknown
    };

    private static string ImportedMethodName(string alias, string name) => "Import_" + SanitizeNativeName(alias) + "_" + SanitizeNativeName(name);

    private static string EscapeStringForAttribute(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string FfiCSharpType(VelaType type) => type.Name switch
    {
        "Bool" => "byte",
        "Int" => "int",
        "UInt" => "uint",
        "Long" => "long",
        "Float" => "float",
        "Double" => "double",
        "Decimal" => "VelaDecimal",
        "Text" => "VelaText",
        "Unit" => "void",
        "Tuple" when type.TypeArguments.Count is >= 2 and <= 8 => $"({string.Join(", ", type.TypeArguments.Select(CSharpType))})",
        _ => "nint"
    };

    private static string FfiIncomingCode(VelaType type, string code) => type.Name switch
    {
        "Bool" => $"{code} != 0",
        "Decimal" => $"{code}.ToDecimal()",
        "Text" => $"{code}.ToManagedString()",
        _ => code
    };

    private static string FfiOutgoingCode(VelaType type, string code) => type.Name switch
    {
        "Bool" => $"(byte)({code} ? 1 : 0)",
        "Decimal" => $"VelaDecimal.FromDecimal({code})",
        "Text" => $"VelaText.FromString({code})",
        _ => code
    };

    private static string SanitizeNativeName(string value)
    {
        var normalized = new string(value.Select(static character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "package" : normalized;
    }
}
