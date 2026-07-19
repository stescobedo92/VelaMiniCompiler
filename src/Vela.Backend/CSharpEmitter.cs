using System.Globalization;
using System.Text;
using Vela.Backend.Emission;
using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Backend;

/// <summary>Performs Vela semantic checks while emitting deterministic, Native AOT-safe C#.</summary>
internal sealed partial class CSharpEmitter
{
    private static readonly string[] EmptyGenericNames = [];
    private static readonly string[] OptionMatchVariants = ["Some", "None"];
    private static readonly string[] ResultMatchVariants = ["Ok", "Err"];
    private static readonly CoreParameter[] SystemExecParameters =
    [
        new("program", VelaType.Text),
        new("args", new VelaType("List", [VelaType.Text])),
        new("timeout_ms", VelaType.Int, "30000"),
        new("max_output_bytes", VelaType.Int, "1048576")
    ];
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
        "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return",
        "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };
    private static readonly HashSet<string> CoreModules = new(StringComparer.Ordinal)
    {
        "vela.core.json", "vela.core.crypto", "vela.core.tcp", "vela.core.text", "vela.core.math",
        "vela.core.time", "vela.core.random", "vela.core.io", "vela.core.encoding", "vela.core.env",
        "vela.core.system", "vela.core.console", "vela.core.gui", "vela.core.http",
        "vela.core.graphql", "vela.core.grpc", "vela.core.sqlite", "vela.core.postgres",
        "vela.concurrent"
    };
    private static readonly Dictionary<string, int> RuntimeExceptionRanks = new(StringComparer.Ordinal)
    {
        ["VelaRuntimeException"] = 0,
        ["VelaIoException"] = 1,
        ["VelaNetworkException"] = 1,
        ["VelaFormatException"] = 1,
        ["VelaOverflowException"] = 1,
        ["VelaArithmeticException"] = 1,
        ["VelaNullReferenceException"] = 1,
        ["VelaIndexOutOfRangeException"] = 1,
        ["VelaInvalidCastException"] = 1,
        ["VelaCancellationException"] = 1,
        ["VelaCleanupException"] = 1,
        ["VelaProcessException"] = 1
    };

    private readonly DiagnosticBag _diagnostics;
    private readonly CodeWriter _writer = new();
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordSymbol> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectSymbol> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumSymbol> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VelaLibraryImport> _imports;
    private readonly Dictionary<string, VelaLibraryImport> _importsByAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _coreModuleAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourcePackageAliases = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _sourcePackages;
    private readonly Func<TextSpan, TextLocation> _getLocation;
    private ObjectSymbol? _currentObject;
    private bool _libraryMode;
    private string _libraryPackageName = string.Empty;
    private readonly List<VelaFfiExport> _ffiExports = [];
    private readonly Stack<string> _deferScopes = new();
    private int _loopDepth;
    private int _switchIdentifier;
    private int _deferScopeIdentifier;
    private int _deferSnapshotIdentifier;
    private int _runtimeExceptionMemberIdentifier;
    private int _destructuringIdentifier;
    private int _callArgumentIdentifier;
    private int _callbackIdentifier;
    private bool _isEmittingAsyncFunction;

    /// <summary>Gets whether the compiled program imports the desktop GUI module.</summary>
    public bool RequiresGui { get; private set; }

    /// <summary>Gets whether the compiled program imports HTTP and/or GraphQL modules.</summary>
    public bool RequiresHttp { get; private set; }

    /// <summary>Gets whether the compiled program imports the gRPC module.</summary>
    public bool RequiresGrpc { get; private set; }

    /// <summary>Gets whether the compiled program imports the SQLite module.</summary>
    public bool RequiresSqlite { get; private set; }

    /// <summary>Gets whether the compiled program imports the PostgreSQL module.</summary>
    public bool RequiresPostgres { get; private set; }

    public CSharpEmitter(
        SourceText source,
        DiagnosticBag diagnostics,
        IReadOnlyList<VelaLibraryImport>? imports = null,
        IReadOnlySet<string>? sourcePackages = null,
        Func<TextSpan, TextLocation>? getLocation = null)
    {
        _diagnostics = diagnostics;
        _imports = (imports ?? []).ToDictionary(static importItem => importItem.PackageName, StringComparer.Ordinal);
        _sourcePackages = sourcePackages ?? new HashSet<string>(StringComparer.Ordinal);
        _getLocation = getLocation ?? source.GetLocation;
    }

    public string Emit(CompilationUnitSyntax root)
    {
        _libraryMode = false;
        _libraryPackageName = string.Empty;
        return EmitCore(root);
    }

    public VelaLibraryEmission EmitLibrary(CompilationUnitSyntax root, string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        _libraryMode = true;
        _libraryPackageName = packageName;
        var source = EmitCore(root);
        return new VelaLibraryEmission(source, _ffiExports.ToArray());
    }

    private string EmitCore(CompilationUnitSyntax root)
    {
        RegisterDeclarations(root);
        RegisterImports(root);

        _writer.WriteLine("// <auto-generated />");
        _writer.WriteLine("using System;");
        _writer.WriteLine("using System.Collections.Generic;");
        _writer.WriteLine("using System.Runtime.InteropServices;");
        _writer.WriteLine("using System.Threading.Tasks;");
        _writer.WriteLine("using Vela.Runtime;");
        if (RequiresGui)
        {
            _writer.WriteLine("using Vela.Ui;");
        }

        if (RequiresHttp)
        {
            _writer.WriteLine("using Vela.Http;");
        }

        if (RequiresGrpc)
        {
            _writer.WriteLine("using Vela.Grpc;");
        }

        if (RequiresSqlite)
        {
            _writer.WriteLine("using Vela.Sqlite;");
        }

        if (RequiresPostgres)
        {
            _writer.WriteLine("using Vela.Postgres;");
        }

        _writer.WriteLine();
        _writer.WriteLine("namespace Vela.Generated;");
        _writer.WriteLine();

        foreach (var declaration in root.Members.OfType<EnumDeclarationSyntax>())
        {
            EmitEnum(declaration);
            _writer.WriteLine();
        }

        foreach (var record in root.Members.OfType<RecordDeclarationSyntax>())
        {
            EmitRecord(record);
            _writer.WriteLine();
        }

        foreach (var declaration in root.Members.OfType<ObjectDeclarationSyntax>())
        {
            EmitObject(declaration);
            _writer.WriteLine();
        }

        if (_importsByAlias.Count > 0)
        {
            EmitImportedBindings();
            _writer.WriteLine();
        }

        _writer.WriteLine("public static class Program");
        _writer.WriteLine("{");
        _writer.Indent();
        if (!_libraryMode)
        {
            EmitEntryPoint(root);
        }

        foreach (var function in root.Members.OfType<FunctionDeclarationSyntax>())
        {
            _writer.WriteLine();
            EmitFunction(function);
        }

        var topLevelStatements = root.Members.Where(static member => member is not FunctionDeclarationSyntax and not RecordDeclarationSyntax and not ObjectDeclarationSyntax and not EnumDeclarationSyntax and not IncludeDirectiveSyntax).ToArray();
        if (topLevelStatements.Length > 0)
        {
            _writer.WriteLine();
            EmitScript(topLevelStatements);
        }

        _writer.Unindent();
        _writer.WriteLine("}");
        if (_libraryMode)
        {
            _writer.WriteLine();
            EmitNativeExports(root);
        }

        _writer.WriteLine("#line default");
        return _writer.ToString();
    }

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
        _writer.WriteLine($"[UnmanagedCallersOnly(EntryPoint = \"{symbol}\", CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
        _writer.WriteLine($"public static {FfiCSharpType(returnType)} {EscapeIdentifier(function.Identifier.Text)}({parameters})");
        _writer.WriteLine("{");
        _writer.Indent();
        var arguments = function.Parameters.Select((parameter, index) => FfiIncomingCode(parameterTypes[index], EscapeIdentifier(parameter.Identifier.Text)));
        var invocation = $"Program.{EscapeIdentifier(function.Identifier.Text)}({string.Join(", ", arguments)})";
        if (returnType.IsSameAs(VelaType.Unit))
        {
            _writer.WriteLine(invocation + ";");
            _writer.WriteLine("return;");
        }
        else
        {
            _writer.WriteLine($"return {FfiOutgoingCode(returnType, invocation)};");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
        _writer.WriteLine();
    }

    private void RegisterDeclarations(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            switch (member)
            {
                case FunctionDeclarationSyntax function:
                    AddDeclaration(_functions, function.Identifier.Text, new FunctionSymbol(function), function.Identifier.Span, "function");
                    break;
                case RecordDeclarationSyntax record:
                    AddDeclaration(_records, record.Identifier.Text, new RecordSymbol(record), record.Identifier.Span, "record");
                    break;
                case ObjectDeclarationSyntax declaration:
                    AddDeclaration(_objects, declaration.Identifier.Text, new ObjectSymbol(declaration), declaration.Identifier.Span, declaration.Kind.ToString().ToLowerInvariant());
                    break;
                case EnumDeclarationSyntax declaration:
                    AddDeclaration(_enums, declaration.Identifier.Text, new EnumSymbol(declaration), declaration.Identifier.Span, "enum");
                    break;
            }
        }
    }

    private void RegisterImports(CompilationUnitSyntax root)
    {
        foreach (var include in root.Members.OfType<IncludeDirectiveSyntax>())
        {
            if (string.Equals(include.PackageName, "vela.core", StringComparison.Ordinal))
            {
                continue;
            }

            if (_sourcePackages.Contains(include.PackageName))
            {
                var sourceAlias = include.Alias?.Text ?? include.PackageSegments[^1].Text;
                if (_sourcePackageAliases.TryGetValue(sourceAlias, out var existingSourcePackage))
                {
                    if (!string.Equals(existingSourcePackage, include.PackageName, StringComparison.Ordinal))
                    {
                        Report("VEL3000", include.Span, $"Duplicate package alias '{sourceAlias}'.", "Use a unique alias after 'as'.");
                    }

                    continue;
                }

                if (_importsByAlias.ContainsKey(sourceAlias) || _coreModuleAliases.ContainsKey(sourceAlias))
                {
                    Report("VEL3000", include.Span, $"Duplicate package alias '{sourceAlias}'.", "Use a unique alias after 'as'.");
                    continue;
                }

                _sourcePackageAliases.Add(sourceAlias, include.PackageName);
                continue;
            }

            if (include.PackageName.StartsWith("vela.core.", StringComparison.Ordinal) || string.Equals(include.PackageName, "vela.concurrent", StringComparison.Ordinal))
            {
                if (!CoreModules.Contains(include.PackageName))
                {
                    Report("VEL3014", include.Span, $"Unknown Vela core module '{include.PackageName}'.", "Import one of the documented vela.core modules.");
                    continue;
                }

                if (string.Equals(include.PackageName, "vela.core.gui", StringComparison.Ordinal))
                {
                    RequiresGui = true;
                }

                if (string.Equals(include.PackageName, "vela.core.http", StringComparison.Ordinal)
                    || string.Equals(include.PackageName, "vela.core.graphql", StringComparison.Ordinal))
                {
                    RequiresHttp = true;
                }

                if (string.Equals(include.PackageName, "vela.core.grpc", StringComparison.Ordinal))
                {
                    RequiresGrpc = true;
                }

                if (string.Equals(include.PackageName, "vela.core.sqlite", StringComparison.Ordinal))
                {
                    RequiresSqlite = true;
                }

                if (string.Equals(include.PackageName, "vela.core.postgres", StringComparison.Ordinal))
                {
                    RequiresPostgres = true;
                }

                var coreAlias = include.Alias?.Text ?? include.PackageSegments[^1].Text;
                if (_coreModuleAliases.TryGetValue(coreAlias, out var existingCoreModule))
                {
                    if (!string.Equals(existingCoreModule, include.PackageName, StringComparison.Ordinal))
                    {
                        Report("VEL3000", include.Span, $"Duplicate package alias '{coreAlias}'.", "Use a unique alias after 'as'.");
                    }

                    continue;
                }

                if (!_coreModuleAliases.TryAdd(coreAlias, include.PackageName) || _importsByAlias.ContainsKey(coreAlias) || _sourcePackageAliases.ContainsKey(coreAlias))
                {
                    Report("VEL3000", include.Span, $"Duplicate package alias '{coreAlias}'.", "Use a unique alias after 'as'.");
                }

                continue;
            }

            if (!_imports.TryGetValue(include.PackageName, out var importItem))
            {
                Report("VEL3014", include.Span, $"Package '{include.PackageName}' is not declared by the current build.", "Declare it in vela.toml and build the locked dependency graph.");
                continue;
            }

            var alias = include.Alias?.Text ?? include.PackageSegments[^1].Text;
            if (!_importsByAlias.TryAdd(alias, importItem) || _coreModuleAliases.ContainsKey(alias))
            {
                Report("VEL3000", include.Span, $"Duplicate package alias '{alias}'.", "Use a unique alias after 'as'.");
            }
        }
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
                var needsWireWrapper = RequiresWireWrapper(returnType) || parameterTypes.Any(RequiresWireWrapper);
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
                _writer.WriteLine($"[LibraryImport(\"{EscapeStringForAttribute(pair.Value.LibraryName)}\", EntryPoint = \"{EscapeStringForAttribute(exportItem.Symbol)}\")]");
                _writer.WriteLine("[UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]");
                _writer.WriteLine($"private static partial {FfiCSharpType(returnType)} {rawName}({rawParameters});");
                _writer.WriteLine();

                var managedParameters = string.Join(", ", parameterTypes.Select((type, index) => $"{CSharpType(type)} value{index.ToString(CultureInfo.InvariantCulture)}"));
                _writer.WriteLine($"internal static {CSharpType(returnType)} {methodName}({managedParameters})");
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

                if (returnType.IsSameAs(VelaType.Unit))
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
                    _writer.WriteLine($"return {FfiIncomingCode(returnType, $"{rawName}({callArgs})")};");
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

    private void AddDeclaration<TSymbol>(Dictionary<string, TSymbol> symbols, string name, TSymbol symbol, TextSpan span, string kind)
    {
        if (!symbols.TryAdd(name, symbol))
        {
            Report("VEL3000", span, $"Duplicate {kind} declaration '{name}'.", "Rename one declaration so every symbol in this scope is unique.");
        }
    }

    private void EmitEntryPoint(CompilationUnitSyntax root)
    {
        _writer.WriteLine("public static int Main(string[] args)");
        _writer.WriteLine("{");
        _writer.Indent();
        _writer.WriteLine("VelaEnvironment.Initialize(args);");

        if (_functions.TryGetValue("main", out var main))
        {
            if (main.Syntax.Parameters.Count != 0)
            {
                Report("VEL3006", main.Syntax.Identifier.Span, "The entry function 'main' cannot declare parameters.", "Declare 'fn main() -> Int:' without parameters.");
            }

            if (main.GenericNames.Length != 0)
            {
                Report("VEL3006", main.Syntax.Identifier.Span, "The entry function 'main' cannot declare generic parameters.", "Declare a non-generic 'fn main() -> Int:'.");
            }

            var returnType = ResolveType(main.Syntax.ReturnType, main.GenericNames, main.Syntax.Identifier.Span, defaultType: VelaType.WholeNumber, allowVoid: true);
            if (!returnType.IsSameAs(VelaType.WholeNumber) && !returnType.IsSameAs(VelaType.Unit))
            {
                Report("VEL3020", main.Syntax.Identifier.Span, "The entry function 'main' must return Int or Void.", "Change the declaration to 'fn main() -> Int { ... }' or 'fn main() -> Void { ... }'.");
            }

            var invocation = main.Syntax.AsyncKeyword is null ? "main()" : "main().GetAwaiter().GetResult()";
            if (returnType.IsSameAs(VelaType.Unit))
            {
                _writer.WriteLine($"{invocation};");
                _writer.WriteLine("return 0;");
            }
            else
            {
                _writer.WriteLine($"return checked((int){invocation});");
            }
        }
        else if (root.Members.Any(static member => member is not FunctionDeclarationSyntax and not RecordDeclarationSyntax and not ObjectDeclarationSyntax and not EnumDeclarationSyntax and not IncludeDirectiveSyntax))
        {
            _writer.WriteLine("return __script();");
        }
        else
        {
            _writer.WriteLine("return 0;");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitEnum(EnumDeclarationSyntax declaration)
    {
        ValidateAttributes(declaration.Attributes ?? [], AttributeTarget.Type);
        var accessibility = declaration.PublicKeyword is null ? "internal" : "public";
        WriteLineDirective(declaration);
        _writer.WriteLine($"{accessibility} enum {EscapeIdentifier(declaration.Identifier.Text)}");
        _writer.WriteLine("{");
        _writer.Indent();
        for (var index = 0; index < declaration.Members.Count; index++)
        {
            var member = declaration.Members[index];
            ValidateAttributes(member.Attributes ?? [], AttributeTarget.EnumMember);
            var suffix = index == declaration.Members.Count - 1 ? string.Empty : ",";
            _writer.WriteLine(EscapeIdentifier(member.Identifier.Text) + suffix);
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitRecord(RecordDeclarationSyntax record)
    {
        ValidateAttributes(record.Attributes ?? [], AttributeTarget.Type);
        var symbol = _records[record.Identifier.Text];
        var genericNames = symbol.GenericNames;
        var fields = record.Members.OfType<RecordFieldSyntax>().ToArray();
        foreach (var field in fields)
        {
            ValidateAttributes(field.Attributes ?? [], AttributeTarget.Field);
        }

        foreach (var method in record.Members.OfType<RecordMethodSyntax>())
        {
            Report("VEL3010", method.Span, "Record methods are not available in this compiler version.", "Move the behavior to a generic function and pass the record as an argument.");
        }

        WriteLineDirective(record);
        var genericSuffix = FormatGenericParameterNames(genericNames);
        var parameters = string.Join(", ", fields.Select(field =>
            $"{CSharpType(ResolveType(field.Type, genericNames, field.Type.Span))} {EscapeIdentifier(field.Identifier.Text)}"));
        _writer.WriteLine($"public sealed record {EscapeIdentifier(record.Identifier.Text)}{genericSuffix}({parameters});");
    }

    private void EmitObject(ObjectDeclarationSyntax declaration)
    {
        var symbol = _objects[declaration.Identifier.Text];
        ValidateAttributes(declaration.Attributes ?? [], AttributeTarget.Type);
        ValidateGenericConstraints(declaration.GenericParameters);
        var genericNames = symbol.GenericNames;
        var accessibility = declaration.PublicKeyword is null ? "internal" : "public";
        var genericSuffix = FormatGenericParameterNames(genericNames);
        var implementedTypes = declaration.ImplementedInterfaces
            .Select(type => ResolveType(type, genericNames, type.Span))
            .Select(CSharpType)
            .ToArray();

        if (declaration.Kind == ObjectDeclarationKind.Interface)
        {
            if (declaration.ImplementsKeyword is not null)
            {
                Report("VEL3006", declaration.ImplementsKeyword.Span, "An interface cannot implement another interface.", "List inherited interfaces after the interface name in a future language version.");
            }

            _writer.WriteLine($"{accessibility} interface {EscapeIdentifier(declaration.Identifier.Text)}{genericSuffix}");
            _writer.WriteLine("{");
            _writer.Indent();
            foreach (var method in declaration.Members.OfType<InterfaceMethodSyntax>())
            {
                ValidateAttributes(method.Attributes ?? [], AttributeTarget.Method);
                ValidateGenericConstraints(method.GenericParameters);
                var parameters = string.Join(", ", method.Parameters.Select(parameter =>
                    $"{CSharpType(ResolveType(parameter.Type, genericNames, parameter.Type.Span))} {EscapeIdentifier(parameter.Identifier.Text)}"));
                var returnType = ResolveType(method.ReturnType, genericNames, method.Identifier.Span, VelaType.Unit, allowVoid: true);
                var emittedReturnType = method.AsyncKeyword is null ? CSharpType(returnType) : CSharpTaskType(returnType);
                _writer.WriteLine($"{emittedReturnType} {EscapeIdentifier(method.Identifier.Text)}({parameters});");
            }

            _writer.Unindent();
            _writer.WriteLine("}");
            return;
        }

        ValidateImplementedInterfaces(symbol);
        var kind = declaration.Kind == ObjectDeclarationKind.Struct ? "struct" : "class";
        var suffix = implementedTypes.Length == 0 ? string.Empty : " : " + string.Join(", ", implementedTypes);
        _writer.WriteLine($"{accessibility} {kind} {EscapeIdentifier(declaration.Identifier.Text)}{genericSuffix}{suffix}");
        _writer.WriteLine("{");
        _writer.Indent();

        var fields = declaration.Members.OfType<ObjectFieldSyntax>().ToArray();
        var constructorParameters = declaration.ConstructorParameters;
        var constructorScope = new Scope(null);
        var methodConstructorScope = new Scope(null);
        if (declaration.Kind == ObjectDeclarationKind.Class)
        {
            foreach (var parameter in constructorParameters)
            {
                var parameterType = ResolveType(parameter.Type, genericNames, parameter.Type.Span);
                ValidateParameterDefault(parameter, parameterType, constructorScope);
                AddVariable(constructorScope, parameter.Identifier.Text, new VariableSymbol(parameterType, false), parameter.Identifier.Span);
                var captureName = ConstructorCaptureName(parameter.Identifier.Text);
                AddVariable(methodConstructorScope, parameter.Identifier.Text, new VariableSymbol(parameterType, false, $"this.{captureName}"), parameter.Identifier.Span);
                _writer.WriteLine($"private readonly {CSharpType(parameterType)} {captureName};");
            }

            if (constructorParameters.Count > 0)
            {
                _writer.WriteLine();
            }
        }

        foreach (var field in fields)
        {
            ValidateAttributes(field.Attributes ?? [], AttributeTarget.Field);
            var modifier = field.IsMutable ? "public" : "public readonly";
            var fieldType = ResolveType(field.Type, genericNames, field.Type.Span);
            _writer.WriteLine($"{modifier} {CSharpType(fieldType)} {EscapeIdentifier(field.Identifier.Text)};");
        }

        if (declaration.Kind == ObjectDeclarationKind.Class)
        {
            if (fields.Length > 0 || constructorParameters.Count > 0)
            {
                _writer.WriteLine();
            }

            var parameters = string.Join(", ", constructorParameters.Select(parameter =>
                $"{CSharpType(ResolveType(parameter.Type, genericNames, parameter.Type.Span))} {EscapeIdentifier(parameter.Identifier.Text)}"));
            _writer.WriteLine($"public {EscapeIdentifier(declaration.Identifier.Text)}({parameters})");
            _writer.WriteLine("{");
            _writer.Indent();
            foreach (var parameter in constructorParameters)
            {
                _writer.WriteLine($"this.{ConstructorCaptureName(parameter.Identifier.Text)} = {EscapeIdentifier(parameter.Identifier.Text)};");
            }

            foreach (var field in fields)
            {
                var fieldType = ResolveType(field.Type, genericNames, field.Type.Span);
                if (field.Initializer is null)
                {
                    Report("VEL3021", field.Identifier.Span, $"Class field '{field.Identifier.Text}' must have an initializer.", "Initialize the field from a primary constructor parameter or another expression.");
                    _writer.WriteLine($"this.{EscapeIdentifier(field.Identifier.Text)} = {DefaultValue(fieldType)};");
                    continue;
                }

                var initializer = EmitExpression(field.Initializer, constructorScope);
                EnsureAssignable(fieldType, initializer.Type, field.Initializer.Span);
                _writer.WriteLine($"this.{EscapeIdentifier(field.Identifier.Text)} = {CoerceCode(fieldType, initializer, field.Initializer)};");
            }

            _writer.Unindent();
            _writer.WriteLine("}");
        }
        else if (fields.Length > 0)
        {
            _writer.WriteLine();
            foreach (var field in fields.Where(static field => field.Initializer is not null))
            {
                Report("VEL3021", field.Initializer!.Span, "Struct fields cannot declare initializers in this language version.", "Pass field values when constructing the struct.");
            }

            var parameters = string.Join(", ", fields.Select(field =>
                $"{CSharpType(ResolveType(field.Type, genericNames, field.Type.Span))} {EscapeIdentifier(field.Identifier.Text)}"));
            _writer.WriteLine($"public {EscapeIdentifier(declaration.Identifier.Text)}({parameters})");
            _writer.WriteLine("{");
            _writer.Indent();
            foreach (var field in fields)
            {
                _writer.WriteLine($"this.{EscapeIdentifier(field.Identifier.Text)} = {EscapeIdentifier(field.Identifier.Text)};");
            }

            _writer.Unindent();
            _writer.WriteLine("}");
        }

        var previousObject = _currentObject;
        _currentObject = symbol;
        try
        {
            foreach (var method in declaration.Members.OfType<ObjectMethodSyntax>())
            {
                _writer.WriteLine();
                EmitObjectMethod(method.Function, genericNames, methodConstructorScope);
            }
        }
        finally
        {
            _currentObject = previousObject;
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitObjectMethod(
        FunctionDeclarationSyntax function,
        IReadOnlyCollection<string> objectGenericNames,
        Scope constructorScope)
    {
        ValidateAttributes(function.Attributes ?? [], AttributeTarget.Method);
        ValidateGenericConstraints(function.GenericParameters);
        var genericNames = function.GenericParameters.Select(static parameter => parameter.Identifier.Text).ToArray();
        var returnType = ResolveType(function.ReturnType, genericNames.Concat(objectGenericNames).ToArray(), function.Identifier.Span, VelaType.Unit, allowVoid: true);
        var parameters = new List<string>();
        var scope = new Scope(constructorScope);

        foreach (var parameter in function.Parameters)
        {
            var parameterType = ResolveType(parameter.Type, genericNames.Concat(objectGenericNames).ToArray(), parameter.Type.Span);
            ValidateParameterDefault(parameter, parameterType, scope);
            AddVariable(scope, parameter.Identifier.Text, new VariableSymbol(parameterType, false), parameter.Identifier.Span);
            parameters.Add($"{CSharpType(parameterType)} {EscapeIdentifier(parameter.Identifier.Text)}");
        }

        if (function.AsyncKeyword is not null && function.FfiKeyword is not null)
        {
            Report("VEL3019", function.AsyncKeyword.Span, "An async function cannot be exported through the native FFI.", "Expose a synchronous ABI-safe wrapper instead.");
        }

        WriteLineDirective(function);
        var asyncModifier = function.AsyncKeyword is null ? string.Empty : "async ";
        var emittedReturnType = function.AsyncKeyword is null ? CSharpType(returnType) : CSharpTaskType(returnType);
        _writer.WriteLine($"public {asyncModifier}{emittedReturnType} {EscapeIdentifier(function.Identifier.Text)}{FormatGenericParameterNames(genericNames)}({string.Join(", ", parameters)})");
        _writer.WriteLine("{");
        _writer.Indent();
        var previousAsyncContext = _isEmittingAsyncFunction;
        _isEmittingAsyncFunction = function.AsyncKeyword is not null;
        bool alwaysReturns;
        try
        {
            alwaysReturns = EmitBlock(function.Body, scope, returnType, isTailBlock: true);
        }
        finally
        {
            _isEmittingAsyncFunction = previousAsyncContext;
        }
        if (!alwaysReturns && !returnType.IsSameAs(VelaType.Unit))
        {
            Report("VEL3007", function.Identifier.Span, $"Method '{function.Identifier.Text}' does not return {returnType} on every path.", "Return a value explicitly or end every control-flow branch with an expression.");
            _writer.WriteLine($"return {DefaultValue(returnType)};");
        }
        else if (!alwaysReturns)
        {
            _writer.WriteLine("return;");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void ValidateImplementedInterfaces(ObjectSymbol symbol)
    {
        var seenInterfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeSyntax in symbol.Syntax.ImplementedInterfaces)
        {
            var implementedType = ResolveType(typeSyntax, symbol.GenericNames, typeSyntax.Span);
            if (!seenInterfaces.Add(implementedType.ToString()))
            {
                Report("VEL3022", typeSyntax.Span, $"Interface '{implementedType}' is implemented more than once.", "Remove the duplicate interface from the implements list.");
                continue;
            }

            if (typeSyntax is not NamedTypeSyntax named
                || !_objects.TryGetValue(named.Identifier.Text, out var interfaceSymbol)
                || interfaceSymbol.Syntax.Kind != ObjectDeclarationKind.Interface)
            {
                Report("VEL3022", typeSyntax.Span, $"Unknown interface '{implementedType}'.", "Declare the interface before implementing it and verify the generic arguments.");
                continue;
            }

            var interfaceSubstitutions = CreateGenericSubstitutions(interfaceSymbol.GenericNames, implementedType.TypeArguments.ToArray());

            foreach (var required in interfaceSymbol.Syntax.Members.OfType<InterfaceMethodSyntax>())
            {
                var implementation = symbol.Syntax.Members.OfType<ObjectMethodSyntax>()
                    .Select(static method => method.Function)
                    .FirstOrDefault(candidate => candidate.Identifier.Text == required.Identifier.Text
                        && candidate.GenericParameters.Count == required.GenericParameters.Count
                        && candidate.Parameters.Count == required.Parameters.Count);
                if (implementation is null)
                {
                    Report("VEL3022", symbol.Syntax.Identifier.Span, $"Class '{symbol.Syntax.Identifier.Text}' does not implement '{interfaceSymbol.Syntax.Identifier.Text}.{FormatInterfaceSignature(required)}'.", "Add a method with exactly the required parameters, return type, generic arity, and async modifier.");
                    continue;
                }

                if ((required.AsyncKeyword is not null) != (implementation.AsyncKeyword is not null))
                {
                    Report("VEL3022", implementation.Identifier.Span, $"Method '{implementation.Identifier.Text}' does not match the async contract required by '{interfaceSymbol.Syntax.Identifier.Text}'.", "Mark both declarations async or make both synchronous.");
                }

                var expectedReturn = ResolveType(required.ReturnType, interfaceSymbol.GenericNames, required.Identifier.Span, VelaType.Unit, allowVoid: true).Substitute(interfaceSubstitutions);
                var actualReturn = ResolveType(implementation.ReturnType, implementation.GenericParameters.Select(static parameter => parameter.Identifier.Text).Concat(symbol.GenericNames).ToArray(), implementation.Identifier.Span, VelaType.Unit, allowVoid: true);
                if (!expectedReturn.IsSameAs(actualReturn))
                {
                    Report("VEL3022", implementation.ReturnType?.Span ?? implementation.Identifier.Span, $"Method '{implementation.Identifier.Text}' returns {actualReturn}, but interface '{interfaceSymbol.Syntax.Identifier.Text}' requires {expectedReturn}.", "Use the exact return type declared by the interface.");
                }

                for (var index = 0; index < required.Parameters.Count; index++)
                {
                    var expected = ResolveType(required.Parameters[index].Type, interfaceSymbol.GenericNames, required.Parameters[index].Type.Span).Substitute(interfaceSubstitutions);
                    var actual = ResolveType(implementation.Parameters[index].Type, symbol.GenericNames, implementation.Parameters[index].Type.Span);
                    if (!expected.IsSameAs(actual))
                    {
                        Report("VEL3022", implementation.Parameters[index].Type.Span, $"Parameter '{implementation.Parameters[index].Identifier.Text}' has type {actual}, but interface '{interfaceSymbol.Syntax.Identifier.Text}' requires {expected}.", "Use the exact parameter type declared by the interface.");
                    }
                }
            }
        }
    }

    private static string FormatInterfaceSignature(InterfaceMethodSyntax method) =>
        $"{method.Identifier.Text}({string.Join(", ", method.Parameters.Select(static parameter => parameter.Type.ToString()))})";

    private void EmitFunction(FunctionDeclarationSyntax function)
    {
        ValidateAttributes(function.Attributes ?? [], AttributeTarget.Function);
        var symbol = _functions[function.Identifier.Text];
        var genericNames = symbol.GenericNames;
        ValidateGenericConstraints(function.GenericParameters);
        var returnType = ResolveType(function.ReturnType, genericNames, function.Identifier.Span, defaultType: VelaType.Unit, allowVoid: true);
        var parameters = new List<string>();
        var scope = new Scope(null);

        foreach (var parameter in function.Parameters)
        {
            var parameterType = ResolveType(parameter.Type, genericNames, parameter.Type.Span);
            ValidateParameterDefault(parameter, parameterType, scope);
            AddVariable(scope, parameter.Identifier.Text, new VariableSymbol(parameterType, false), parameter.Identifier.Span);
            parameters.Add($"{CSharpType(parameterType)} {EscapeIdentifier(parameter.Identifier.Text)}");
        }

        if (function.AsyncKeyword is not null && function.FfiKeyword is not null)
        {
            Report("VEL3019", function.AsyncKeyword.Span, "An async function cannot be exported through the native FFI.", "Expose a synchronous ABI-safe wrapper instead.");
        }

        WriteLineDirective(function);
        var asyncModifier = function.AsyncKeyword is null ? string.Empty : "async ";
        var emittedReturnType = function.AsyncKeyword is null ? CSharpType(returnType) : CSharpTaskType(returnType);
        _writer.WriteLine($"internal static {asyncModifier}{emittedReturnType} {EscapeIdentifier(function.Identifier.Text)}{FormatGenericParameterNames(genericNames)}({string.Join(", ", parameters)})");
        _writer.WriteLine("{");
        _writer.Indent();
        var previousAsyncContext = _isEmittingAsyncFunction;
        _isEmittingAsyncFunction = function.AsyncKeyword is not null;
        bool alwaysReturns;
        try
        {
            alwaysReturns = EmitBlock(function.Body, scope, returnType, isTailBlock: true);
        }
        finally
        {
            _isEmittingAsyncFunction = previousAsyncContext;
        }
        if (!alwaysReturns && !returnType.IsSameAs(VelaType.Unit))
        {
            Report("VEL3007", function.Identifier.Span, $"Function '{function.Identifier.Text}' does not return {returnType} on every path.", "Return a value explicitly or end every control-flow branch with an expression.");
            _writer.WriteLine($"return {DefaultValue(returnType)};");
        }
        else if (!alwaysReturns && returnType.IsSameAs(VelaType.Unit))
        {
            _writer.WriteLine("return;");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitScript(IReadOnlyList<StatementSyntax> statements)
    {
        _writer.WriteLine("private static int __script()");
        _writer.WriteLine("{");
        _writer.Indent();
        var scope = new Scope(null);
        _ = EmitStatements(statements, scope, VelaType.WholeNumber, tailReturnsValue: false);
        _writer.WriteLine("return 0;");
        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private bool EmitBlock(BlockSyntax block, Scope parent, VelaType returnType, bool isTailBlock)
    {
        var scope = new Scope(parent);
        if (!block.Statements.Any(static statement => statement is DeferStatementSyntax))
        {
            return EmitStatements(block.Statements, scope, returnType, isTailBlock);
        }

        var identifier = _deferScopeIdentifier++.ToString(CultureInfo.InvariantCulture);
        var deferScope = "__velaDefers" + identifier;
        var primaryException = "__velaPrimaryException" + identifier;
        _writer.WriteLine($"var {deferScope} = new VelaDeferScope();");
        _writer.WriteLine($"Exception {primaryException} = null;");
        _writer.WriteLine("try");
        _writer.WriteLine("{");
        _writer.Indent();
        _deferScopes.Push(deferScope);
        bool returns;
        try
        {
            returns = EmitStatements(block.Statements, scope, returnType, isTailBlock);
        }
        finally
        {
            _ = _deferScopes.Pop();
        }

        _writer.Unindent();
        _writer.WriteLine("}");
        _writer.WriteLine($"catch (Exception __velaFailure{identifier})");
        _writer.WriteLine("{");
        _writer.Indent();
        _writer.WriteLine($"{primaryException} = __velaFailure{identifier};");
        _writer.WriteLine("throw;");
        _writer.Unindent();
        _writer.WriteLine("}");
        _writer.WriteLine("finally");
        _writer.WriteLine("{");
        _writer.Indent();
        _writer.WriteLine($"{deferScope}.Run({primaryException});");
        _writer.Unindent();
        _writer.WriteLine("}");
        return returns;
    }

    private bool EmitStatements(IReadOnlyList<StatementSyntax> statements, Scope scope, VelaType returnType, bool tailReturnsValue)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            var isTail = tailReturnsValue && index == statements.Count - 1;
            var returns = EmitStatement(statements[index], scope, returnType, isTail);
            if (returns && index < statements.Count - 1)
            {
                Report("VEL3011", statements[index + 1].Span, "Unreachable statement after a return.", "Remove the statement or move it before the return.");
            }

            if (isTail)
            {
                return returns;
            }
        }

        return false;
    }

    private bool EmitStatement(StatementSyntax statement, Scope scope, VelaType returnType, bool isTail)
    {
        WriteLineDirective(statement);
        switch (statement)
        {
            case LetStatementSyntax let:
                EmitVariable(let.Identifier.Text, let.Type, let.Initializer, false, scope, let.Span);
                return false;
            case VarStatementSyntax variable:
                EmitVariable(variable.Identifier.Text, variable.Type, variable.Initializer, true, scope, variable.Span);
                return false;
            case TupleDestructuringStatementSyntax tupleDestructuring:
                EmitTupleDestructuring(tupleDestructuring, scope);
                return false;
            case RecordDestructuringStatementSyntax recordDestructuring:
                EmitRecordDestructuring(recordDestructuring, scope);
                return false;
            case ReturnStatementSyntax returnStatement:
                EmitReturn(returnStatement, scope, returnType);
                return true;
            case AssertStatementSyntax assertion:
                EmitAssert(assertion, scope);
                return false;
            case DeferStatementSyntax defer:
                EmitDefer(defer, scope);
                return false;
            case TryStatementSyntax protectedStatement:
                return EmitTry(protectedStatement, scope, returnType, isTail);
            case ExpressionStatementSyntax expressionStatement:
                return EmitExpressionStatement(expressionStatement.Expression, scope, returnType, isTail);
            case IfStatementSyntax conditional:
                return EmitIf(conditional, scope, returnType, isTail);
            case ForStatementSyntax loop:
                EmitFor(loop, scope, returnType);
                return false;
            case WhileStatementSyntax loop:
                EmitWhile(loop, scope, returnType);
                return false;
            case BreakStatementSyntax breakStatement:
                return EmitLoopControl(breakStatement.BreakKeyword, "break");
            case ContinueStatementSyntax continueStatement:
                return EmitLoopControl(continueStatement.ContinueKeyword, "continue");
            case SwitchStatementSyntax selection:
                return EmitSwitch(selection, scope, returnType, isTail);
            case MatchStatementSyntax match:
                return EmitMatch(match, scope, returnType, isTail);
            case FunctionDeclarationSyntax:
            case RecordDeclarationSyntax:
                Report("VEL3012", statement.Span, "Declarations are only allowed at the top level.", "Move this declaration outside the current function or block.");
                return false;
            default:
                Report("VEL3013", statement.Span, "Unsupported statement.");
                return false;
        }
    }

    private void EmitVariable(string name, TypeSyntax? declaredTypeSyntax, ExpressionSyntax initializer, bool mutable, Scope scope, TextSpan span)
    {
        var initializerResult = EmitExpression(initializer, scope);
        var declaredType = declaredTypeSyntax is null
            ? initializerResult.Type
            : ResolveType(declaredTypeSyntax, EmptyGenericNames, declaredTypeSyntax.Span);
        EnsureAssignable(declaredType, initializerResult.Type, initializer.Span);
        AddVariable(scope, name, new VariableSymbol(declaredType, mutable), span);
        _writer.WriteLine($"{CSharpType(declaredType)} {EscapeIdentifier(name)} = {CoerceCode(declaredType, initializerResult, initializer)};");
    }

    private void EmitTupleDestructuring(TupleDestructuringStatementSyntax statement, Scope scope)
    {
        var value = EmitExpression(statement.Initializer, scope);
        if (value.Type.Name != "Tuple")
        {
            Report("VEL3025", statement.Initializer.Span, $"Cannot tuple-destructure a value of type '{value.Type}'.", "Use a tuple value with the same number of elements.");
        }
        else if (value.Type.TypeArguments.Count != statement.Bindings.Count)
        {
            Report("VEL3025", statement.Span, $"Tuple destructuring expects {statement.Bindings.Count} element(s), but the value contains {value.Type.TypeArguments.Count}.", "Use exactly one binding per tuple element.");
        }

        var temporary = "__velaDestructure" + _destructuringIdentifier++.ToString(CultureInfo.InvariantCulture);
        _writer.WriteLine($"var {temporary} = {value.Code};");
        for (var index = 0; index < statement.Bindings.Count; index++)
        {
            var binding = statement.Bindings[index];
            if (binding.Text == "_")
            {
                continue;
            }

            var elementType = value.Type.Name == "Tuple" && index < value.Type.TypeArguments.Count
                ? value.Type.TypeArguments[index]
                : VelaType.Unknown;
            AddVariable(scope, binding.Text, new VariableSymbol(elementType, false), binding.Span);
            _writer.WriteLine($"{CSharpType(elementType)} {EscapeIdentifier(binding.Text)} = {temporary}.Item{(index + 1).ToString(CultureInfo.InvariantCulture)};");
        }
    }

    private void EmitRecordDestructuring(RecordDestructuringStatementSyntax statement, Scope scope)
    {
        var value = EmitExpression(statement.Initializer, scope);
        if (!_records.TryGetValue(statement.RecordType.Text, out var record))
        {
            Report("VEL3025", statement.RecordType.Span, $"Unknown record '{statement.RecordType.Text}' in destructuring pattern.", "Use a declared record type.");
            return;
        }

        if (!string.Equals(value.Type.Name, statement.RecordType.Text, StringComparison.Ordinal))
        {
            Report("VEL3025", statement.Initializer.Span, $"Record destructuring expects '{statement.RecordType.Text}', but found '{value.Type}'.", "Use a value of the record type named by the pattern.");
        }

        var fields = record.Syntax.Members.OfType<RecordFieldSyntax>().ToDictionary(static field => field.Identifier.Text, StringComparer.Ordinal);
        var namedFields = statement.Fields.Where(static field => field.Text != "_").Select(static field => field.Text).ToHashSet(StringComparer.Ordinal);
        var missing = fields.Keys.Where(name => !namedFields.Contains(name)).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            Report("VEL3025", statement.Span, $"Record destructuring is missing field(s): {string.Join(", ", missing)}.", "List every record field exactly once or use tuple destructuring for positional data.");
        }

        var temporary = "__velaDestructure" + _destructuringIdentifier++.ToString(CultureInfo.InvariantCulture);
        _writer.WriteLine($"var {temporary} = {value.Code};");
        var substitutions = CreateGenericSubstitutions(record.GenericNames, value.Type.TypeArguments.ToArray());
        foreach (var binding in statement.Fields)
        {
            if (binding.Text == "_")
            {
                continue;
            }

            if (!fields.TryGetValue(binding.Text, out var field))
            {
                Report("VEL3025", binding.Span, $"Record '{statement.RecordType.Text}' has no field named '{binding.Text}'.", "Use one of the declared record fields.");
                continue;
            }

            var fieldType = ResolveType(field.Type, record.GenericNames, field.Type.Span).Substitute(substitutions);
            AddVariable(scope, binding.Text, new VariableSymbol(fieldType, false), binding.Span);
            _writer.WriteLine($"{CSharpType(fieldType)} {EscapeIdentifier(binding.Text)} = {temporary}.{EscapeIdentifier(binding.Text)};");
        }
    }

    private void EmitReturn(ReturnStatementSyntax statement, Scope scope, VelaType returnType)
    {
        if (statement.Expression is null)
        {
            if (!returnType.IsSameAs(VelaType.Unit))
            {
                Report("VEL3007", statement.ReturnKeyword.Span, $"A function returning {returnType} requires a value.", "Return an expression with the declared return type.");
            }

            _writer.WriteLine("return;");
            return;
        }

        var expression = EmitExpression(statement.Expression, scope);
        if (returnType.IsSameAs(VelaType.Unit))
        {
            Report("VEL3020", statement.Expression.Span, "A Void function cannot return a value.", "Remove the expression and use 'return;'.");
            _writer.WriteLine($"{expression.Code};");
            _writer.WriteLine("return;");
            return;
        }

        EnsureAssignable(returnType, expression.Type, statement.Expression.Span);
        _writer.WriteLine($"return {CoerceCode(returnType, expression, statement.Expression)};");
    }

    private void EmitAssert(AssertStatementSyntax assertion, Scope scope)
    {
        var condition = EmitExpression(assertion.Condition, scope);
        EnsureAssignable(VelaType.Bool, condition.Type, assertion.Condition.Span);
        var messageCode = QuoteString("Vela assertion failed.");
        if (assertion.Message is not null)
        {
            var messageExpression = EmitExpression(assertion.Message, scope);
            EnsureAssignable(VelaType.Text, messageExpression.Type, assertion.Message.Span);
            messageCode = messageExpression.Code;
        }

        _writer.WriteLine($"Contract.Require({condition.Code}, {messageCode});");
    }

    private bool EmitTry(TryStatementSyntax protectedStatement, Scope scope, VelaType returnType, bool isTail)
    {
        _writer.WriteLine("try");
        _writer.WriteLine("{");
        _writer.Indent();
        var tryReturns = EmitBlock(protectedStatement.TryBlock, scope, returnType, isTail);
        _writer.Unindent();
        _writer.WriteLine("}");

        var catchReturns = new List<bool>();
        var caughtTypes = new HashSet<string>(StringComparer.Ordinal);
        var caughtBaseException = false;
        foreach (var catchClause in protectedStatement.Catches)
        {
            var exceptionType = ResolveType(catchClause.ExceptionType, EmptyGenericNames, catchClause.ExceptionType.Span);
            if (!RuntimeExceptionRanks.TryGetValue(exceptionType.Name, out var rank))
            {
                Report("VEL3018", catchClause.ExceptionType.Span, $"Type '{exceptionType}' cannot be used in a Vela catch clause.", "Catch one of the documented Vela runtime exception types.");
            }
            else
            {
                if (!caughtTypes.Add(exceptionType.Name))
                {
                    Report("VEL3018", catchClause.ExceptionType.Span, $"Duplicate catch for '{exceptionType.Name}'.", "Keep one handler for each exception type.");
                }

                if (caughtBaseException && rank > 0)
                {
                    Report("VEL3018", catchClause.ExceptionType.Span, $"Catch for '{exceptionType.Name}' is unreachable after VelaRuntimeException.", "Place more-specific exception types before VelaRuntimeException.");
                }

                caughtBaseException |= rank == 0;
            }

            var catchScope = new Scope(scope);
            AddVariable(catchScope, catchClause.Identifier.Text, new VariableSymbol(exceptionType, false), catchClause.Identifier.Span);
            _writer.WriteLine($"catch ({CSharpType(exceptionType)} {EscapeIdentifier(catchClause.Identifier.Text)})");
            _writer.WriteLine("{");
            _writer.Indent();
            catchReturns.Add(EmitBlock(catchClause.Block, catchScope, returnType, isTail));
            _writer.Unindent();
            _writer.WriteLine("}");
        }

        if (protectedStatement.FinallyClause is not null)
        {
            _writer.WriteLine("finally");
            _writer.WriteLine("{");
            _writer.Indent();
            _ = EmitBlock(protectedStatement.FinallyClause.Block, scope, returnType, isTailBlock: false);
            _writer.Unindent();
            _writer.WriteLine("}");
        }

        return isTail && tryReturns && (catchReturns.Count == 0 || catchReturns.All(static returns => returns));
    }

    private void EmitDefer(DeferStatementSyntax defer, Scope scope)
    {
        if (!_deferScopes.TryPeek(out var deferScope))
        {
            Report("VEL3017", defer.Span, "A defer statement is only valid inside a block.", "Move the deferred call into a function or control-flow block.");
            return;
        }

        if (defer.Invocation is not CallExpressionSyntax call)
        {
            Report("VEL3017", defer.Invocation.Span, "A defer statement requires a call expression.", "Use syntax such as 'defer tcp.close(connection);'.");
            return;
        }

        var snapshotScope = new Scope(scope);
        var callee = SnapshotDeferredCallee(call.Callee, scope, snapshotScope);
        var arguments = new List<CallArgumentSyntax>(call.Arguments.Count);
        foreach (var argument in call.Arguments)
        {
            var value = EmitExpression(argument.Expression, scope);
            var name = CreateDeferSnapshot(value, snapshotScope);
            arguments.Add(new CallArgumentSyntax(
                argument.Name,
                argument.ColonToken,
                new NameExpressionSyntax(new SyntaxToken(TokenKind.Identifier, argument.Span, name))));
        }

        var snapshot = new CallExpressionSyntax(
            callee,
            call.LessToken,
            call.TypeArguments,
            call.GreaterToken,
            call.LeftParenthesis,
            arguments,
            call.RightParenthesis);
        var invocation = EmitCall(snapshot, snapshotScope);
        _writer.WriteLine($"{deferScope}.Push(() => {{ {invocation.Code}; }});");
    }

    private ExpressionSyntax SnapshotDeferredCallee(ExpressionSyntax callee, Scope scope, Scope snapshotScope)
    {
        if (callee is NameExpressionSyntax)
        {
            return callee;
        }

        if (callee is MemberAccessExpressionSyntax member)
        {
            if (member.Receiver is NameExpressionSyntax { Identifier.Text: var alias }
                && (_coreModuleAliases.ContainsKey(alias) || _importsByAlias.ContainsKey(alias)))
            {
                return member;
            }

            var receiver = EmitExpression(member.Receiver, scope);
            var name = CreateDeferSnapshot(receiver, snapshotScope);
            return new MemberAccessExpressionSyntax(
                new NameExpressionSyntax(new SyntaxToken(TokenKind.Identifier, member.Receiver.Span, name)),
                member.DotToken,
                member.Member);
        }

        Report("VEL3017", callee.Span, "A deferred call must have a named function or member target.", "Call a function directly or defer an instance/core-module method call.");
        return callee;
    }

    private string CreateDeferSnapshot(ExpressionResult value, Scope scope)
    {
        var name = "__velaDeferValue" + _deferSnapshotIdentifier++.ToString(CultureInfo.InvariantCulture);
        _writer.WriteLine($"var {name} = {value.Code};");
        AddVariable(scope, name, new VariableSymbol(value.Type, false), default);
        return name;
    }

    private bool EmitExpressionStatement(ExpressionSyntax expression, Scope scope, VelaType returnType, bool isTail)
    {
        var result = EmitExpression(expression, scope);
        if (isTail && !returnType.IsSameAs(VelaType.Unit))
        {
            EnsureAssignable(returnType, result.Type, expression.Span);
            _writer.WriteLine($"return {result.Code};");
            return true;
        }

        _writer.WriteLine($"{result.Code};");
        return false;
    }

    private bool EmitIf(IfStatementSyntax conditional, Scope scope, VelaType returnType, bool isTail)
    {
        var condition = EmitExpression(conditional.Condition, scope);
        EnsureAssignable(VelaType.Bool, condition.Type, conditional.Condition.Span);
        _writer.WriteLine($"if ({condition.Code})");
        _writer.WriteLine("{");
        _writer.Indent();
        var thenReturns = EmitBlock(conditional.ThenBlock, scope, returnType, isTail);
        _writer.Unindent();
        _writer.WriteLine("}");

        if (conditional.ElseClause is null)
        {
            return false;
        }

        _writer.WriteLine("else");
        _writer.WriteLine("{");
        _writer.Indent();
        var elseReturns = EmitBlock(conditional.ElseClause.Block, scope, returnType, isTail);
        _writer.Unindent();
        _writer.WriteLine("}");
        return isTail && thenReturns && elseReturns;
    }

    private void EmitFor(ForStatementSyntax loop, Scope scope, VelaType returnType)
    {
        var collection = EmitExpression(loop.Collection, scope);
        var elementType = GetIterationElementType(collection.Type, loop.Collection.Span);
        var loopScope = new Scope(scope);
        AddVariable(loopScope, loop.Identifier.Text, new VariableSymbol(elementType, false), loop.Identifier.Span);

        _writer.WriteLine($"foreach (var {EscapeIdentifier(loop.Identifier.Text)} in {collection.Code})");
        _writer.WriteLine("{");
        _writer.Indent();
        _loopDepth++;
        try
        {
            _ = EmitBlock(loop.Body, loopScope, returnType, isTailBlock: false);
        }
        finally
        {
            _loopDepth--;
        }
        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private void EmitWhile(WhileStatementSyntax loop, Scope scope, VelaType returnType)
    {
        var condition = EmitExpression(loop.Condition, scope);
        EnsureAssignable(VelaType.Bool, condition.Type, loop.Condition.Span);
        _writer.WriteLine($"while ({condition.Code})");
        _writer.WriteLine("{");
        _writer.Indent();
        _loopDepth++;
        try
        {
            _ = EmitBlock(loop.Body, scope, returnType, isTailBlock: false);
        }
        finally
        {
            _loopDepth--;
        }

        _writer.Unindent();
        _writer.WriteLine("}");
    }

    private bool EmitLoopControl(SyntaxToken keyword, string emittedKeyword)
    {
        if (_loopDepth == 0)
        {
            Report("VEL3015", keyword.Span, $"'{emittedKeyword}' is only valid inside a loop.", "Place it inside a while or for block.");
        }

        _writer.WriteLine(emittedKeyword + ";");
        return true;
    }

    private bool EmitSwitch(SwitchStatementSyntax selection, Scope scope, VelaType returnType, bool isTail)
    {
        var subject = EmitExpression(selection.Expression, scope);
        var isEnum = _enums.TryGetValue(subject.Type.Name, out var enumSymbol);
        if (!isEnum && subject.Type.Name is not "Int" and not "UInt" and not "Long" and not "Bool" and not "Text")
        {
            Report("VEL3006", selection.Expression.Span, $"Switch does not support values of type '{subject.Type}'.", "Use Int, UInt, Long, Bool, Text, or an enum.");
        }

        var subjectName = "__velaSwitch" + _switchIdentifier++.ToString(CultureInfo.InvariantCulture);
        _writer.WriteLine($"var {subjectName} = {subject.Code};");
        var seenCases = new HashSet<string>(StringComparer.Ordinal);
        var hasCase = false;
        var everyCaseReturns = true;
        foreach (var switchCase in selection.Cases)
        {
            var value = EmitExpression(switchCase.Value, scope);
            if (!isEnum && switchCase.Value is not LiteralExpressionSyntax)
            {
                Report("VEL3006", switchCase.Value.Span, "Switch case values must be literals.", "Use an Int, UInt, Long, Bool, or Text literal.");
            }

            if (isEnum && switchCase.Value is not MemberAccessExpressionSyntax)
            {
                Report("VEL3006", switchCase.Value.Span, $"Switch cases for enum '{subject.Type}' must use a qualified enum member.", $"Use '{subject.Type}.Member'.");
            }

            if (!subject.Type.IsSameAs(value.Type))
            {
                if (subject.Type.IsNumeric && value.Type.IsNumeric)
                {
                    value = new ExpressionResult(subject.Type, CoerceNumeric(subject.Type, value, switchCase.Value));
                }
                else
                {
                    Report("VEL3002", switchCase.Value.Span, $"Switch case type '{value.Type}' does not match subject type '{subject.Type}'.");
                }
            }

            var key = subject.Type + "|" + value.Code;
            if (!seenCases.Add(key))
            {
                Report("VEL3000", switchCase.Value.Span, "Duplicate switch case value.", "Keep each case value unique.");
            }

            _writer.WriteLine(hasCase ? $"else if ({subjectName} == {value.Code})" : $"if ({subjectName} == {value.Code})");
            _writer.WriteLine("{");
            _writer.Indent();
            var caseReturns = EmitBlock(switchCase.Body, scope, returnType, isTailBlock: isTail);
            _writer.Unindent();
            _writer.WriteLine("}");
            hasCase = true;
            everyCaseReturns &= caseReturns;
        }

        var isExhaustiveEnum = isEnum && enumSymbol!.Syntax.Members.All(member => seenCases.Contains($"{subject.Type}|{EscapeIdentifier(subject.Type.Name)}.{EscapeIdentifier(member.Identifier.Text)}"));
        var defaultReturns = false;
        if (selection.DefaultClause is not null)
        {
            _writer.WriteLine(hasCase ? "else" : "if (true)");
            _writer.WriteLine("{");
            _writer.Indent();
            defaultReturns = EmitBlock(selection.DefaultClause.Body, scope, returnType, isTailBlock: isTail);
            _writer.Unindent();
            _writer.WriteLine("}");
        }

        if (isEnum && selection.DefaultClause is null && !isExhaustiveEnum)
        {
            var missing = enumSymbol!.Syntax.Members
                .Where(member => !seenCases.Contains($"{subject.Type}|{EscapeIdentifier(subject.Type.Name)}.{EscapeIdentifier(member.Identifier.Text)}"))
                .Select(member => member.Identifier.Text)
                .ToArray();
            Report("VEL3016", selection.Span, $"Switch over enum '{subject.Type}' is not exhaustive; missing: {string.Join(", ", missing)}.", "Handle every enum member or add a default block.");
        }

        if (isExhaustiveEnum && selection.DefaultClause is null)
        {
            _writer.WriteLine("else");
            _writer.WriteLine("{");
            _writer.Indent();
            _writer.WriteLine("throw new InvalidOperationException(\"Unreachable exhaustive Vela enum switch.\");");
            _writer.Unindent();
            _writer.WriteLine("}");
        }

        return isTail && everyCaseReturns && (defaultReturns || isExhaustiveEnum);
    }

    private bool EmitMatch(MatchStatementSyntax match, Scope scope, VelaType returnType, bool isTail)
    {
        var subject = EmitExpression(match.Expression, scope);
        var isOption = subject.Type.Name == "Option" && subject.Type.TypeArguments.Count == 1;
        var isResult = subject.Type.Name == "Result" && subject.Type.TypeArguments.Count == 2;
        var isEnum = _enums.TryGetValue(subject.Type.Name, out var enumSymbol);
        var isLiteralType = subject.Type.Name is "Int" or "UInt" or "Long" or "Bool" or "Text";
        if (!isOption && !isResult && !isEnum && !isLiteralType)
        {
            Report("VEL3024", match.Expression.Span, $"Match does not support values of type '{subject.Type}'.", "Use an enum, Option, Result, Int, UInt, Long, Bool, or Text value.");
        }

        var subjectName = "__velaMatch" + _switchIdentifier++.ToString(CultureInfo.InvariantCulture);
        _writer.WriteLine($"var {subjectName} = {subject.Code};");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasCase = false;
        var everyCaseReturns = match.Cases.Count > 0;
        foreach (var matchCase in match.Cases)
        {
            var caseScope = new Scope(scope);
            string condition;
            switch (matchCase.Pattern)
            {
                case VariantMatchPatternSyntax variant:
                    condition = BindVariantPattern(variant, subject.Type, subjectName, isOption, isResult, caseScope, seen);
                    break;
                case ValueMatchPatternSyntax valuePattern:
                    if (isOption || isResult)
                    {
                        Report("VEL3024", valuePattern.Span, $"Type '{subject.Type}' requires variant patterns.", isOption ? "Use Some(value) or None." : "Use Ok(value) or Err(error).");
                    }

                    var value = EmitExpression(valuePattern.Value, scope);
                    if (isEnum && valuePattern.Value is not MemberAccessExpressionSyntax)
                    {
                        Report("VEL3024", valuePattern.Span, $"Enum match cases for '{subject.Type}' must use qualified members.", $"Use '{subject.Type}.Member'.");
                    }
                    else if (!isEnum && valuePattern.Value is not LiteralExpressionSyntax)
                    {
                        Report("VEL3024", valuePattern.Span, "Primitive match cases must be literals.", "Use a literal value or add a default block.");
                    }

                    EnsureAssignable(subject.Type, value.Type, valuePattern.Span);
                    var key = "value|" + value.Code;
                    if (!seen.Add(key))
                    {
                        Report("VEL3024", valuePattern.Span, "Duplicate match pattern.", "Keep each match pattern unique.");
                    }

                    condition = $"{subjectName} == {CoerceCode(subject.Type, value, valuePattern)}";
                    break;
                default:
                    Report("VEL3024", matchCase.Pattern.Span, "Unsupported match pattern.");
                    condition = "false";
                    break;
            }

            _writer.WriteLine(hasCase ? $"else if ({condition})" : $"if ({condition})");
            _writer.WriteLine("{");
            _writer.Indent();
            everyCaseReturns &= EmitBlock(matchCase.Body, caseScope, returnType, isTailBlock: isTail);
            _writer.Unindent();
            _writer.WriteLine("}");
            hasCase = true;
        }

        var isExhaustive = isOption && seen.Contains("variant|Some") && seen.Contains("variant|None")
            || isResult && seen.Contains("variant|Ok") && seen.Contains("variant|Err")
            || isEnum && enumSymbol!.Syntax.Members.All(member => seen.Contains("value|" + EscapeIdentifier(subject.Type.Name) + "." + EscapeIdentifier(member.Identifier.Text)));

        var defaultReturns = false;
        if (match.DefaultClause is not null)
        {
            if (isExhaustive)
            {
                Report("VEL3024", match.DefaultClause.Span, "The default block is unreachable because all variants are already matched.", "Remove the default block or one of the exhaustive cases.");
            }

            _writer.WriteLine(hasCase ? "else" : "if (true)");
            _writer.WriteLine("{");
            _writer.Indent();
            defaultReturns = EmitBlock(match.DefaultClause.Body, scope, returnType, isTailBlock: isTail);
            _writer.Unindent();
            _writer.WriteLine("}");
        }
        else if (!isExhaustive)
        {
            var missing = GetMissingMatchPatterns(subject.Type, enumSymbol, seen);
            Report("VEL3024", match.Span, $"Match over '{subject.Type}' is not exhaustive; missing: {string.Join(", ", missing)}.", "Handle every variant/member or add a default block.");
        }
        else
        {
            _writer.WriteLine("else");
            _writer.WriteLine("{");
            _writer.Indent();
            _writer.WriteLine("throw new InvalidOperationException(\"Unreachable exhaustive Vela match.\");");
            _writer.Unindent();
            _writer.WriteLine("}");
        }

        return isTail && everyCaseReturns && (defaultReturns || isExhaustive);
    }

    private string BindVariantPattern(
        VariantMatchPatternSyntax pattern,
        VelaType subjectType,
        string subjectName,
        bool isOption,
        bool isResult,
        Scope caseScope,
        HashSet<string> seen)
    {
        var variant = pattern.Variant.Text;
        var expectedBinding = variant is "Some" or "Ok" or "Err";
        var valid = isOption && variant is "Some" or "None"
            || isResult && variant is "Ok" or "Err";
        if (!valid)
        {
            Report("VEL3024", pattern.Variant.Span, $"Variant '{variant}' is not valid for '{subjectType}'.", isOption ? "Use Some(value) or None." : isResult ? "Use Ok(value) or Err(error)." : "Use a literal or qualified enum member pattern.");
        }

        if (expectedBinding && pattern.Binding is null)
        {
            Report("VEL3024", pattern.Span, $"Variant '{variant}' requires exactly one binding.", $"Use '{variant}(value)'.");
        }
        else if (!expectedBinding && pattern.Binding is not null)
        {
            Report("VEL3024", pattern.Binding.Span, $"Variant '{variant}' does not carry a value.", $"Use '{variant}' without parentheses.");
        }

        if (!seen.Add("variant|" + variant))
        {
            Report("VEL3024", pattern.Span, $"Duplicate match pattern '{variant}'.", "Keep each variant pattern unique.");
        }

        if (pattern.Binding is not null && pattern.Binding.Text != "_" && valid && expectedBinding)
        {
            var bindingType = variant switch
            {
                "Some" or "Ok" => subjectType.TypeArguments[0],
                "Err" => subjectType.TypeArguments[1],
                _ => VelaType.Unknown
            };
            var bindingCode = variant == "Err" ? $"{subjectName}.Error" : $"{subjectName}.Value";
            AddVariable(caseScope, pattern.Binding.Text, new VariableSymbol(bindingType, false, bindingCode), pattern.Binding.Span);
        }

        return variant switch
        {
            "Some" => $"{subjectName}.HasValue",
            "None" => $"{subjectName}.IsNone",
            "Ok" => $"{subjectName}.IsSuccess",
            "Err" => $"{subjectName}.IsFailure",
            _ => "false"
        };
    }

    private static string[] GetMissingMatchPatterns(VelaType type, EnumSymbol? enumSymbol, HashSet<string> seen)
    {
        if (type.Name == "Option")
        {
            return OptionMatchVariants.Where(name => !seen.Contains("variant|" + name)).ToArray();
        }

        if (type.Name == "Result")
        {
            return ResultMatchVariants.Where(name => !seen.Contains("variant|" + name)).ToArray();
        }

        if (enumSymbol is not null)
        {
            return enumSymbol.Syntax.Members
                .Select(static member => member.Identifier.Text)
                .Where(name => !seen.Contains("value|" + EscapeIdentifier(type.Name) + "." + EscapeIdentifier(name)))
                .ToArray();
        }

        return ["default"];
    }

    private ExpressionResult EmitExpression(ExpressionSyntax expression, Scope scope)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal => EmitLiteral(literal),
            NameExpressionSyntax name => EmitName(name, scope),
            MemberAccessExpressionSyntax member => EmitMember(member, scope),
            IndexExpressionSyntax index => EmitIndex(index, scope),
            UnaryExpressionSyntax unary => EmitUnary(unary, scope),
            AwaitExpressionSyntax awaited => EmitAwait(awaited, scope),
            BinaryExpressionSyntax binary => EmitBinary(binary, scope),
            AssignmentExpressionSyntax assignment => EmitAssignment(assignment, scope),
            ParenthesizedExpressionSyntax parenthesized => EmitParenthesized(parenthesized, scope),
            TupleExpressionSyntax tuple => EmitTuple(tuple, scope),
            CallExpressionSyntax call => EmitCall(call, scope),
            ListExpressionSyntax list => EmitList(list, scope),
            LambdaExpressionSyntax lambda => EmitLambda(lambda, scope),
            _ => UnsupportedExpression(expression)
        };
    }

    private ExpressionResult EmitLambda(LambdaExpressionSyntax lambda, Scope scope)
    {
        var parameterTypes = new List<VelaType>();
        var lambdaScope = new Scope(scope);
        foreach (var parameter in lambda.Parameters)
        {
            var parameterType = ResolveType(parameter.Type, EmptyGenericNames, parameter.Type.Span);
            parameterTypes.Add(parameterType);
            AddVariable(
                lambdaScope,
                parameter.Identifier.Text,
                new VariableSymbol(parameterType, false),
                parameter.Identifier.Span);
        }

        var returnType = ResolveType(lambda.ReturnType, EmptyGenericNames, lambda.ReturnType.Span, allowVoid: true);
        var functionType = CreateFunctionType(parameterTypes.ToArray(), returnType);
        if (!VelaCallbackEmitter.IsSupportedSignature(functionType))
        {
            Report(
                "VEL3030",
                lambda.Span,
                "Supported lambdas accept up to two Bool/Int/UInt/Long/Float/Double/Decimal/Text parameters and return Void or those primitives.",
                "Narrow the callback signature or split the work into a named function.");
        }

        var name = VelaCallbackEmitter.NextCallbackName(ref _callbackIdentifier);
        var previousAsync = _isEmittingAsyncFunction;
        _isEmittingAsyncFunction = false;
        var parameterList = string.Join(
            ", ",
            lambda.Parameters.Select((parameter, index) =>
                $"{CSharpType(parameterTypes[index])} {EscapeIdentifier(parameter.Identifier.Text)}"));
        var returnCSharp = returnType.IsSameAs(VelaType.Unit) ? "void" : CSharpType(returnType);
        _writer.WriteLine($"{returnCSharp} {name}({parameterList})");
        _writer.WriteLine("{");
        _writer.Indent();
        var alwaysReturns = EmitBlock(lambda.Body, lambdaScope, returnType, isTailBlock: true);
        if (!alwaysReturns && returnType.IsSameAs(VelaType.Unit))
        {
            _writer.WriteLine("return;");
        }
        else if (!alwaysReturns)
        {
            Report("VEL3007", lambda.Span, $"Lambda does not return {returnType} on every path.", "Return a value from every path.");
            _writer.WriteLine($"return {DefaultValue(returnType)};");
        }

        _writer.Unindent();
        _writer.WriteLine("}");
        _isEmittingAsyncFunction = previousAsync;
        // Local functions capture outer locals; wrap as a typed delegate for Fn assignments.
        var delegateType = VelaCallbackEmitter.CSharpDelegateType(functionType);
        return new ExpressionResult(functionType, delegateType == "Delegate" ? name : $"new {delegateType}({name})");
    }

    private static VelaType CreateFunctionType(VelaType[] parameterTypes, VelaType returnType)
    {
        var arguments = new VelaType[parameterTypes.Length + 1];
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            arguments[index] = parameterTypes[index];
        }

        arguments[^1] = returnType;
        return new VelaType("Fn", arguments);
    }

    private ExpressionResult EmitAwait(AwaitExpressionSyntax awaited, Scope scope)
    {
        var future = EmitExpression(awaited.Expression, scope);
        if (!_isEmittingAsyncFunction)
        {
            Report("VEL3019", awaited.AwaitKeyword.Span, "'await' is only valid inside an async function.", "Mark the containing function with 'async'.");
        }

        if (!future.Type.IsFuture)
        {
            Report("VEL3019", awaited.Expression.Span, $"Cannot await expression of type '{future.Type}'.", "Await a Future<T> returned by an async function or asynchronous core operation.");
            return new ExpressionResult(VelaType.Unknown, $"await {future.Code}");
        }

        return new ExpressionResult(future.Type.TypeArguments[0], $"await {future.Code}");
    }

    private ExpressionResult EmitLiteral(LiteralExpressionSyntax literal)
    {
        var token = literal.LiteralToken;
        return token.Kind switch
        {
            TokenKind.IntegerLiteral => EmitIntegerLiteral(literal),
            TokenKind.FloatLiteral => new ExpressionResult(VelaType.Double, $"{token.Text}d"),
            TokenKind.StringLiteral => new ExpressionResult(VelaType.Text, QuoteString((string?)token.Value ?? string.Empty)),
            TokenKind.TrueKeyword => new ExpressionResult(VelaType.Bool, "true"),
            TokenKind.FalseKeyword => new ExpressionResult(VelaType.Bool, "false"),
            TokenKind.NilKeyword => new ExpressionResult(VelaType.Nil, "null"),
            _ => UnsupportedExpression(literal)
        };
    }

    private ExpressionResult EmitIntegerLiteral(LiteralExpressionSyntax literal)
    {
        var token = literal.LiteralToken;
        if (token.Value is not long value || value > int.MaxValue)
        {
            Report("VEL3008", token.Span, "Integer literal is outside the Int range.", "Use an explicit Long, UInt, Decimal, Float, or Double conversion.");
            return new ExpressionResult(VelaType.Int, "0");
        }

        return new ExpressionResult(VelaType.Int, token.Text);
    }

    private ExpressionResult EmitName(NameExpressionSyntax name, Scope scope)
    {
        if (name.Identifier.Text == "self" && _currentObject is not null)
        {
            return new ExpressionResult(new VelaType(_currentObject.Syntax.Identifier.Text), "this");
        }

        if (scope.TryLookup(name.Identifier.Text, out var variable))
        {
            return new ExpressionResult(variable.Type, variable.Code ?? EscapeIdentifier(name.Identifier.Text));
        }

        if (_functions.ContainsKey(name.Identifier.Text) || _records.ContainsKey(name.Identifier.Text) || _objects.ContainsKey(name.Identifier.Text) || _enums.ContainsKey(name.Identifier.Text))
        {
            return new ExpressionResult(VelaType.Unknown, EscapeIdentifier(name.Identifier.Text));
        }

        Report("VEL3001", name.Span, $"Unknown name '{name.Identifier.Text}'.", "Declare the name before using it or correct the spelling.");
        return new ExpressionResult(VelaType.Unknown, EscapeIdentifier(name.Identifier.Text));
    }

    private ExpressionResult EmitMember(MemberAccessExpressionSyntax member, Scope scope)
    {
        if (member.Receiver is NameExpressionSyntax enumName && _enums.TryGetValue(enumName.Identifier.Text, out var enumSymbol))
        {
            var enumMember = enumSymbol.Syntax.Members.FirstOrDefault(candidate => candidate.Identifier.Text == member.Member.Text);
            if (enumMember is not null)
            {
                ReportAttributeUse(enumSymbol.Syntax.Attributes ?? [], enumName.Identifier.Span, $"enum '{enumSymbol.Syntax.Identifier.Text}'");
                ReportAttributeUse(enumMember.Attributes ?? [], member.Member.Span, $"enum member '{enumSymbol.Syntax.Identifier.Text}.{member.Member.Text}'");
                return new ExpressionResult(new VelaType(enumSymbol.Syntax.Identifier.Text), $"{EscapeIdentifier(enumName.Identifier.Text)}.{EscapeIdentifier(member.Member.Text)}");
            }

            Report("VEL3009", member.Member.Span, $"Enum '{enumName.Identifier.Text}' does not contain case '{member.Member.Text}'.", "Use a declared enum member.");
            return new ExpressionResult(VelaType.Unknown, $"{EscapeIdentifier(enumName.Identifier.Text)}.{EscapeIdentifier(member.Member.Text)}");
        }

        var receiver = EmitExpression(member.Receiver, scope);
        if (RuntimeExceptionRanks.ContainsKey(receiver.Type.Name))
        {
            var sourceLocationVariable = "__velaSourceLocation" + (_runtimeExceptionMemberIdentifier++).ToString(CultureInfo.InvariantCulture);
            return member.Member.Text switch
            {
                "message" => new ExpressionResult(VelaType.Text, $"{receiver.Code}.Message"),
                "source_location" => new ExpressionResult(new VelaType("Option", [VelaType.Text]), $"{receiver.Code}.SourceLocation is {{ }} {sourceLocationVariable} ? Option.Some({sourceLocationVariable}) : Option.None<string>()"),
                _ => ReportUnknownRuntimeExceptionMember(member, receiver)
            };
        }

        if (receiver.Type.IsSameAs(VelaType.ProcessResult))
        {
            return member.Member.Text switch
            {
                "exit_code" => new ExpressionResult(VelaType.Int, $"{receiver.Code}.ExitCode"),
                "stdout" => new ExpressionResult(VelaType.Text, $"{receiver.Code}.StandardOutput"),
                "stderr" => new ExpressionResult(VelaType.Text, $"{receiver.Code}.StandardError"),
                "timed_out" => new ExpressionResult(VelaType.Bool, $"{receiver.Code}.TimedOut"),
                "truncated" => new ExpressionResult(VelaType.Bool, $"{receiver.Code}.Truncated"),
                _ => ReportUnsupportedMember(member, receiver)
            };
        }

        if (_records.TryGetValue(receiver.Type.Name, out var record))
        {
            var field = record.Syntax.Members.OfType<RecordFieldSyntax>().FirstOrDefault(candidate => candidate.Identifier.Text == member.Member.Text);
            if (field is not null)
            {
                ReportAttributeUse(field.Attributes ?? [], member.Member.Span, $"field '{record.Syntax.Identifier.Text}.{member.Member.Text}'");
                var substitutions = CreateGenericSubstitutions(record.GenericNames, receiver.Type.TypeArguments.ToArray());
                var fieldType = ResolveType(field.Type, record.GenericNames, field.Type.Span).Substitute(substitutions);
                return new ExpressionResult(fieldType, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)}");
            }
        }

        if (_objects.TryGetValue(receiver.Type.Name, out var objectSymbol) && objectSymbol.Syntax.Kind != ObjectDeclarationKind.Interface)
        {
            var field = objectSymbol.Syntax.Members.OfType<ObjectFieldSyntax>().FirstOrDefault(candidate => candidate.Identifier.Text == member.Member.Text);
            if (field is not null)
            {
                ReportAttributeUse(field.Attributes ?? [], member.Member.Span, $"field '{objectSymbol.Syntax.Identifier.Text}.{member.Member.Text}'");
                var substitutions = CreateGenericSubstitutions(objectSymbol.GenericNames, receiver.Type.TypeArguments.ToArray());
                var fieldType = ResolveType(field.Type, objectSymbol.GenericNames, field.Type.Span).Substitute(substitutions);
                return new ExpressionResult(fieldType, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)}");
            }
        }

        if (receiver.Type.IsOptional && receiver.Type.TypeArguments.Count == 1)
        {
            return member.Member.Text switch
            {
                "has_value" => new ExpressionResult(VelaType.Bool, $"{receiver.Code}.HasValue"),
                "value" => new ExpressionResult(receiver.Type.TypeArguments[0], $"VelaGuards.RequireValue({receiver.Code}, {SourceLocationCode(member)})"),
                _ => ReportUnknownOptionalMember(member, receiver)
            };
        }

        if (member.Member.Text == "count" && HasCountProperty(receiver.Type))
        {
            return new ExpressionResult(VelaType.WholeNumber, $"{receiver.Code}.Count");
        }

        if (member.Member.Text == "capacity" && HasCapacityProperty(receiver.Type))
        {
            return new ExpressionResult(VelaType.WholeNumber, $"{receiver.Code}.Capacity");
        }

        if (member.Member.Text == "length" && receiver.Type.Name == "Array")
        {
            return new ExpressionResult(VelaType.Int, $"{receiver.Code}.Length");
        }

        Report("VEL3009", member.Member.Span, $"Type '{receiver.Type}' does not contain member '{member.Member.Text}'.", "Use a declared record field or correct the member name.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)}");
    }

    private ExpressionResult ReportUnknownOptionalMember(MemberAccessExpressionSyntax member, ExpressionResult receiver)
    {
        Report("VEL3009", member.Member.Span, $"Option does not contain member '{member.Member.Text}'.", "Use 'has_value' to test the option or 'value' to read the contained value.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)}");
    }

    private ExpressionResult ReportUnsupportedMember(MemberAccessExpressionSyntax member, ExpressionResult receiver)
    {
        Report("VEL3009", member.Member.Span, $"Type '{receiver.Type}' does not contain member '{member.Member.Text}'.", "Use one of the documented fields for this value.");
        return new ExpressionResult(VelaType.Unknown, "default");
    }

    private ExpressionResult ReportUnknownRuntimeExceptionMember(MemberAccessExpressionSyntax member, ExpressionResult receiver)
    {
        Report("VEL3009", member.Member.Span, $"Runtime exception '{receiver.Type}' does not contain member '{member.Member.Text}'.", "Use 'message' or 'source_location'.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)}");
    }

    private ExpressionResult EmitIndex(IndexExpressionSyntax index, Scope scope)
    {
        var receiver = EmitExpression(index.Receiver, scope);
        var indexValue = EmitExpression(index.Index, scope);
        return receiver.Type.Name switch
        {
            "List" => EmitVectorIndex(index, receiver, indexValue),
            "Array" => EmitArrayIndex(index, receiver, indexValue),
            "HashMap" => EmitHashMapIndex(index, receiver, indexValue),
            "SortedMap" => EmitSortedMapIndex(index, receiver, indexValue),
            _ => ReportUnsupportedIndex(index, receiver, indexValue)
        };
    }

    private ExpressionResult EmitVectorIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        EnsureAssignable(VelaType.WholeNumber, indexValue.Type, index.Index.Span);
        var elementType = receiver.Type.TypeArguments.Count == 1 ? receiver.Type.TypeArguments[0] : VelaType.Unknown;
        return new ExpressionResult(elementType, $"{receiver.Code}.Get({indexValue.Code}, {SourceLocationCode(index)})");
    }

    private ExpressionResult EmitArrayIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        EnsureAssignable(VelaType.Int, indexValue.Type, index.Index.Span);
        var elementType = receiver.Type.TypeArguments.Count == 1 ? receiver.Type.TypeArguments[0] : VelaType.Unknown;
        return new ExpressionResult(elementType, $"{receiver.Code}.Get({indexValue.Code}, {SourceLocationCode(index)})");
    }

    private ExpressionResult EmitHashMapIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        if (receiver.Type.TypeArguments.Count != 2)
        {
            return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
        }

        var keyType = receiver.Type.TypeArguments[0];
        EnsureAssignable(keyType, indexValue.Type, index.Index.Span);
        EnsureHashable(keyType, index.Index.Span);
        return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}]");
    }

    private ExpressionResult EmitSortedMapIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        if (receiver.Type.TypeArguments.Count != 2)
        {
            return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
        }

        var keyType = receiver.Type.TypeArguments[0];
        EnsureAssignable(keyType, indexValue.Type, index.Index.Span);
        EnsureOrdered(keyType, index.Index.Span);
        return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}]");
    }

    private ExpressionResult ReportUnsupportedIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        Report("VEL3009", index.Span, $"Type '{receiver.Type}' does not support indexing.", "Use Vector or HashMap indexing, or call a supported collection method.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
    }

    private ExpressionResult EmitUnary(UnaryExpressionSyntax unary, Scope scope)
    {
        var operand = EmitExpression(unary.Operand, scope);
        if (unary.OperatorToken.Kind == TokenKind.Minus && operand.Type.IsNumeric && !operand.Type.IsSameAs(VelaType.UInt))
        {
            var operation = operand.Type.Name switch
            {
                "Int" => "Negate",
                "Long" => "Negate",
                "Float" => "Negate",
                "Double" => "Negate",
                "Decimal" => "Negate",
                _ => string.Empty
            };
            return new ExpressionResult(operand.Type, $"VelaNumeric.{operation}({operand.Code}, {SourceLocationCode(unary)})");
        }

        Report("VEL3002", unary.Span, $"Operator '{unary.OperatorToken.Text}' cannot be applied to {operand.Type}.");
        return new ExpressionResult(VelaType.Unknown, $"{unary.OperatorToken.Text}{operand.Code}");
    }

    private ExpressionResult EmitBinary(BinaryExpressionSyntax binary, Scope scope)
    {
        var left = EmitExpression(binary.Left, scope);
        var right = EmitExpression(binary.Right, scope);
        var operation = binary.OperatorToken.Kind;

        if (operation is TokenKind.EqualsEquals or TokenKind.BangEquals)
        {
            EnsureComparable(left.Type, right.Type, binary.Span);
            if (left.Type.IsNumeric && right.Type.IsNumeric)
            {
                var comparisonType = GetNumericResultType(left.Type, right.Type, binary.Span);
                return new ExpressionResult(VelaType.Bool, $"{CoerceNumeric(comparisonType, left, binary.Left)} {binary.OperatorToken.Text} {CoerceNumeric(comparisonType, right, binary.Right)}");
            }

            return new ExpressionResult(VelaType.Bool, $"{left.Code} {binary.OperatorToken.Text} {right.Code}");
        }

        if (operation is TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater or TokenKind.GreaterOrEqual)
        {
            if (!left.Type.IsNumeric || !right.Type.IsNumeric)
            {
                Report("VEL3002", binary.Span, $"Operator '{binary.OperatorToken.Text}' requires numeric operands.");
            }

            var comparisonType = GetNumericResultType(left.Type, right.Type, binary.Span);
            return new ExpressionResult(VelaType.Bool, $"{CoerceNumeric(comparisonType, left, binary.Left)} {binary.OperatorToken.Text} {CoerceNumeric(comparisonType, right, binary.Right)}");
        }

        if (operation == TokenKind.Plus && left.Type.IsSameAs(VelaType.Text) && right.Type.IsSameAs(VelaType.Text))
        {
            return new ExpressionResult(VelaType.Text, $"{left.Code} + {right.Code}");
        }

        if (left.Type.IsNumeric && right.Type.IsNumeric)
        {
            var type = GetNumericResultType(left.Type, right.Type, binary.Span);
            var method = operation switch
            {
                TokenKind.Plus => "Add",
                TokenKind.Minus => "Subtract",
                TokenKind.Star => "Multiply",
                TokenKind.Slash => "Divide",
                _ => string.Empty
            };
            return new ExpressionResult(type, $"VelaNumeric.{method}({CoerceNumeric(type, left, binary.Left)}, {CoerceNumeric(type, right, binary.Right)}, {SourceLocationCode(binary)})");
        }

        Report("VEL3002", binary.Span, $"Operator '{binary.OperatorToken.Text}' cannot be applied to {left.Type} and {right.Type}.");
        return new ExpressionResult(VelaType.Unknown, $"{left.Code} {binary.OperatorToken.Text} {right.Code}");
    }

    private ExpressionResult EmitAssignment(AssignmentExpressionSyntax assignment, Scope scope)
    {
        if (assignment.Target is IndexExpressionSyntax index)
        {
            return EmitIndexedAssignment(index, assignment.Value, scope);
        }

        if (assignment.Target is MemberAccessExpressionSyntax memberTarget)
        {
            return EmitMemberAssignment(memberTarget, assignment.Value, scope);
        }

        if (assignment.Target is not NameExpressionSyntax target)
        {
            Report("VEL3004", assignment.Target.Span, "Only named mutable bindings can be assigned.", "Assign to a variable declared with 'var'.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        var value = EmitExpression(assignment.Value, scope);
        if (!scope.TryLookup(target.Identifier.Text, out var variable))
        {
            Report("VEL3001", target.Span, $"Unknown name '{target.Identifier.Text}'.", "Declare the variable before assigning to it.");
            return new ExpressionResult(VelaType.Unknown, $"{EscapeIdentifier(target.Identifier.Text)} = {value.Code}");
        }

        if (!variable.Mutable)
        {
            Report("VEL3004", target.Span, $"Cannot assign to immutable binding '{target.Identifier.Text}'.", "Declare the binding with 'var' if it must change.");
        }

        EnsureAssignable(variable.Type, value.Type, assignment.Value.Span);
        return new ExpressionResult(variable.Type, $"{EscapeIdentifier(target.Identifier.Text)} = {CoerceCode(variable.Type, value, assignment.Value)}");
    }

    private ExpressionResult EmitMemberAssignment(MemberAccessExpressionSyntax member, ExpressionSyntax valueSyntax, Scope scope)
    {
        var receiver = EmitExpression(member.Receiver, scope);
        var value = EmitExpression(valueSyntax, scope);
        if (!_objects.TryGetValue(receiver.Type.Name, out var objectSymbol))
        {
            Report("VEL3004", member.Span, "Only mutable object fields can be assigned.", "Assign to a variable, collection element, or mutable class field.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        var field = objectSymbol.Syntax.Members.OfType<ObjectFieldSyntax>().FirstOrDefault(candidate => candidate.Identifier.Text == member.Member.Text);
        if (field is null)
        {
            Report("VEL3009", member.Member.Span, $"Type '{receiver.Type}' does not contain field '{member.Member.Text}'.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        if (!field.IsMutable)
        {
            Report("VEL3004", member.Member.Span, $"Cannot assign to immutable field '{member.Member.Text}'.", "Prefix the field declaration with 'var' to make it mutable.");
        }

        var expected = ResolveType(field.Type, objectSymbol.GenericNames, field.Type.Span);
        EnsureAssignable(expected, value.Type, valueSyntax.Span);
        return new ExpressionResult(expected, $"{receiver.Code}.{EscapeIdentifier(member.Member.Text)} = {CoerceCode(expected, value, valueSyntax)}");
    }

    private ExpressionResult EmitIndexedAssignment(IndexExpressionSyntax index, ExpressionSyntax valueSyntax, Scope scope)
    {
        var receiver = EmitExpression(index.Receiver, scope);
        var indexValue = EmitExpression(index.Index, scope);
        var value = EmitExpression(valueSyntax, scope);
        switch (receiver.Type.Name)
        {
            case "List" when receiver.Type.TypeArguments.Count == 1:
                EnsureAssignable(VelaType.Int, indexValue.Type, index.Index.Span);
                EnsureAssignable(receiver.Type.TypeArguments[0], value.Type, valueSyntax.Span);
                return new ExpressionResult(receiver.Type.TypeArguments[0], $"{receiver.Code}.Set({indexValue.Code}, {CoerceCode(receiver.Type.TypeArguments[0], value, valueSyntax)}, {SourceLocationCode(index)})");
            case "Array" when receiver.Type.TypeArguments.Count == 1:
                EnsureAssignable(VelaType.Int, indexValue.Type, index.Index.Span);
                EnsureAssignable(receiver.Type.TypeArguments[0], value.Type, valueSyntax.Span);
                return new ExpressionResult(receiver.Type.TypeArguments[0], $"{receiver.Code}.Set({indexValue.Code}, {CoerceCode(receiver.Type.TypeArguments[0], value, valueSyntax)}, {SourceLocationCode(index)})");
            case "HashMap" when receiver.Type.TypeArguments.Count == 2:
                EnsureAssignable(receiver.Type.TypeArguments[0], indexValue.Type, index.Index.Span);
                EnsureHashable(receiver.Type.TypeArguments[0], index.Index.Span);
                EnsureAssignable(receiver.Type.TypeArguments[1], value.Type, valueSyntax.Span);
                return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}] = {CoerceCode(receiver.Type.TypeArguments[1], value, valueSyntax)}");
            case "SortedMap" when receiver.Type.TypeArguments.Count == 2:
                EnsureAssignable(receiver.Type.TypeArguments[0], indexValue.Type, index.Index.Span);
                EnsureOrdered(receiver.Type.TypeArguments[0], index.Index.Span);
                EnsureAssignable(receiver.Type.TypeArguments[1], value.Type, valueSyntax.Span);
                return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}] = {CoerceCode(receiver.Type.TypeArguments[1], value, valueSyntax)}");
            default:
                Report("VEL3009", index.Span, $"Type '{receiver.Type}' does not support indexed assignment.", "Use Vector or HashMap indexing, or call a supported collection method.");
                return new ExpressionResult(VelaType.Unknown, "default");
        }
    }

    private ExpressionResult EmitParenthesized(ParenthesizedExpressionSyntax expression, Scope scope)
    {
        var inner = EmitExpression(expression.Expression, scope);
        return new ExpressionResult(inner.Type, $"({inner.Code})");
    }

    private ExpressionResult EmitTuple(TupleExpressionSyntax tuple, Scope scope)
    {
        var elements = tuple.Elements.Select(element => EmitExpression(element, scope)).ToArray();
        if (elements.Length is < 2 or > 8)
        {
            Report("VEL3025", tuple.Span, "Tuple expressions require between two and eight elements.", "Use a record for larger structured values.");
        }

        return new ExpressionResult(new VelaType("Tuple", elements.Select(static element => element.Type).ToArray()), $"({string.Join(", ", elements.Select(static element => element.Code))})");
    }

    private ExpressionResult EmitCall(CallExpressionSyntax call, Scope scope)
    {
        if (call.Callee is MemberAccessExpressionSyntax member)
        {
            return EmitMemberMethodCall(call, member, scope);
        }

        if (call.Callee is not NameExpressionSyntax name)
        {
            Report("VEL3005", call.Callee.Span, "Only named functions, records, and collection methods can be called.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        var explicitTypes = call.TypeArguments.Select(type => ResolveType(type, EmptyGenericNames, type.Span)).ToArray();
        if (name.Identifier.Text == "print")
        {
            if (arguments.Length != 1)
            {
                Report("VEL3006", call.Span, "Function 'print' expects exactly one argument.");
            }

            var printedValue = arguments.Length == 0 ? "string.Empty" : arguments[0].Code;
            return new ExpressionResult(VelaType.Unit, $"Console.WriteLine({printedValue})");
        }

        if (TryGetNumericType(name.Identifier.Text, out var numericType))
        {
            return EmitNumericConversion(call, arguments, explicitTypes, numericType);
        }

        if (name.Identifier.Text is "unbox" or "try_unbox")
        {
            return EmitUnbox(call, arguments, explicitTypes, name.Identifier.Text == "try_unbox");
        }

        if (name.Identifier.Text is "some" or "none" or "ok" or "err")
        {
            return EmitAlgebraicConstruction(call, name.Identifier.Text, arguments, explicitTypes);
        }

        if (IsCollectionConstructor(name.Identifier.Text))
        {
            return EmitCollectionConstruction(call, name.Identifier.Text, arguments, explicitTypes);
        }

        if (_functions.TryGetValue(name.Identifier.Text, out var function))
        {
            return EmitFunctionCall(call, function, arguments, explicitTypes);
        }

        if (_records.TryGetValue(name.Identifier.Text, out var record))
        {
            return EmitRecordConstruction(call, record, arguments, explicitTypes);
        }

        if (_objects.TryGetValue(name.Identifier.Text, out var objectSymbol) && objectSymbol.Syntax.Kind != ObjectDeclarationKind.Interface)
        {
            return EmitObjectConstruction(call, objectSymbol, arguments, explicitTypes);
        }

        if (scope.TryLookup(name.Identifier.Text, out var callback) && string.Equals(callback.Type.Name, "Fn", StringComparison.Ordinal))
        {
            return EmitFunctionValueCall(call, name, callback, arguments, explicitTypes);
        }

        Report("VEL3005", name.Span, $"Unknown function or record '{name.Identifier.Text}'.", "Declare it before calling it or correct the spelling.");
        return new ExpressionResult(VelaType.Unknown, $"{EscapeIdentifier(name.Identifier.Text)}({string.Join(", ", arguments.Select(static argument => argument.Code))})");
    }

    private ExpressionResult EmitFunctionValueCall(
        CallExpressionSyntax call,
        NameExpressionSyntax name,
        VariableSymbol callback,
        ExpressionResult[] arguments,
        VelaType[] explicitTypes)
    {
        if (explicitTypes.Length != 0)
        {
            Report("VEL3006", call.Span, "Function values do not accept explicit type arguments.");
        }

        var parameterTypes = callback.Type.TypeArguments.Take(callback.Type.TypeArguments.Count - 1).ToArray();
        var returnType = callback.Type.TypeArguments[^1];
        if (arguments.Length != parameterTypes.Length)
        {
            Report("VEL3006", call.Span, $"Function value '{name.Identifier.Text}' expects {parameterTypes.Length} argument(s), but received {arguments.Length}.");
        }

        var argumentCodes = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index < parameterTypes.Length)
            {
                EnsureAssignable(parameterTypes[index], arguments[index].Type, call.Arguments[index].Span);
                argumentCodes.Add(CoerceCode(parameterTypes[index], arguments[index], call.Arguments[index]));
            }
            else
            {
                argumentCodes.Add(arguments[index].Code);
            }
        }

        var target = callback.Code ?? EscapeIdentifier(name.Identifier.Text);
        return new ExpressionResult(returnType, $"{target}({string.Join(", ", argumentCodes)})");
    }

    private ExpressionResult EmitNumericConversion(CallExpressionSyntax call, ExpressionResult[] arguments, VelaType[] explicitTypes, VelaType targetType)
    {
        if (explicitTypes.Length != 0)
        {
            Report("VEL3006", call.Span, $"Conversion '{targetType}' does not accept type arguments.");
        }

        if (arguments.Length != 1)
        {
            Report("VEL3006", call.Span, $"Conversion '{targetType}' expects exactly one numeric argument.");
        }

        var argument = arguments.Length == 0 ? new ExpressionResult(VelaType.Int, "0") : arguments[0];
        if (!argument.Type.IsNumeric)
        {
            Report("VEL3002", call.Arguments.Count == 0 ? call.Span : call.Arguments[0].Span, $"Conversion '{targetType}' requires a numeric value.");
        }

        var method = targetType.Name switch
        {
            "Int" => "ToInt",
            "UInt" => "ToUInt",
            "Long" => "ToLong",
            "Float" => "ToFloat",
            "Double" => "ToDouble",
            "Decimal" => "ToDecimal",
            _ => throw new InvalidOperationException($"Unsupported numeric conversion '{targetType}'.")
        };
        return new ExpressionResult(targetType, $"VelaNumeric.{method}({argument.Code}, {SourceLocationCode(call)})");
    }

    private ExpressionResult EmitUnbox(CallExpressionSyntax call, ExpressionResult[] arguments, VelaType[] explicitTypes, bool optional)
    {
        if (explicitTypes.Length != 1)
        {
            Report("VEL3006", call.Span, $"Function '{(optional ? "try_unbox" : "unbox")}' expects exactly one type argument.");
        }

        if (arguments.Length != 1)
        {
            Report("VEL3006", call.Span, $"Function '{(optional ? "try_unbox" : "unbox")}' expects exactly one boxed value.");
        }

        var targetType = explicitTypes.Length == 0 ? VelaType.Unknown : explicitTypes[0];
        var argument = arguments.Length == 0 ? new ExpressionResult(VelaType.Any, "null") : arguments[0];
        EnsureAssignable(VelaType.Any, argument.Type, call.Arguments.Count == 0 ? call.Span : call.Arguments[0].Span);
        var typeArgument = CSharpType(targetType);
        var code = optional
            ? $"VelaAny.TryUnbox<{typeArgument}>({argument.Code})"
            : $"VelaAny.Unbox<{typeArgument}>({argument.Code}, {SourceLocationCode(call)})";
        return new ExpressionResult(optional ? new VelaType("Option", [targetType]) : targetType, code);
    }

    private ExpressionResult EmitAlgebraicConstruction(
        CallExpressionSyntax call,
        string factory,
        ExpressionResult[] arguments,
        VelaType[] explicitTypes)
    {
        if (call.Arguments.Any(static argument => argument.IsNamed))
        {
            Report("VEL3023", call.Span, $"Factory '{factory}' accepts positional arguments only.", "Remove the argument name and keep the explicit generic type arguments.");
        }

        var isOption = factory is "some" or "none";
        var expectedTypeCount = isOption ? 1 : 2;
        var expectedArgumentCount = factory == "none" ? 0 : 1;
        if (explicitTypes.Length != expectedTypeCount)
        {
            Report("VEL3006", call.Span, $"Factory '{factory}' expects exactly {expectedTypeCount} type argument(s).", isOption ? $"Use '{factory}<ValueType>(...)'." : $"Use '{factory}<ValueType, ErrorType>(...)'.");
        }

        if (arguments.Length != expectedArgumentCount)
        {
            Report("VEL3006", call.Span, $"Factory '{factory}' expects exactly {expectedArgumentCount} value argument(s).");
        }

        var valueType = explicitTypes.Length > 0 ? explicitTypes[0] : VelaType.Unknown;
        var errorType = explicitTypes.Length > 1 ? explicitTypes[1] : VelaType.Unknown;
        if (arguments.Length > 0)
        {
            var expectedValueType = factory == "err" ? errorType : valueType;
            EnsureAssignable(expectedValueType, arguments[0].Type, call.Arguments[0].Span);
        }

        if (isOption)
        {
            var optionType = new VelaType("Option", [valueType]);
            return factory == "none"
                ? new ExpressionResult(optionType, $"Option.None<{CSharpType(valueType)}>()")
                : new ExpressionResult(optionType, $"Option.Some<{CSharpType(valueType)}>({(arguments.Length == 0 ? DefaultValue(valueType) : CoerceCode(valueType, arguments[0], call.Arguments[0].Expression))})");
        }

        var resultType = new VelaType("Result", [valueType, errorType]);
        if (factory == "err")
        {
            var errorCode = arguments.Length == 0 ? DefaultValue(errorType) : CoerceCode(errorType, arguments[0], call.Arguments[0].Expression);
            return new ExpressionResult(resultType, $"Result.Fail<{CSharpType(valueType)}, {CSharpType(errorType)}>({errorCode})");
        }

        var valueCode = arguments.Length == 0 ? DefaultValue(valueType) : CoerceCode(valueType, arguments[0], call.Arguments[0].Expression);
        return new ExpressionResult(resultType, $"Result.Ok<{CSharpType(valueType)}, {CSharpType(errorType)}>({valueCode})");
    }

    private ExpressionResult EmitMemberMethodCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, Scope scope)
    {
        if (member.Receiver is NameExpressionSyntax { Identifier.Text: var coreAlias } && _coreModuleAliases.TryGetValue(coreAlias, out var coreModule))
        {
            return EmitCoreModuleCall(call, member, coreModule, scope);
        }

        if (member.Receiver is NameExpressionSyntax { Identifier.Text: var alias } && _importsByAlias.TryGetValue(alias, out var importItem))
        {
            return EmitImportedFunctionCall(call, member, importItem, scope);
        }

        if (member.Receiver is NameExpressionSyntax { Identifier.Text: var sourceAlias } && _sourcePackageAliases.TryGetValue(sourceAlias, out var sourcePackage))
        {
            return EmitSourcePackageFunctionCall(call, member, sourcePackage, scope);
        }

        var receiver = EmitExpression(member.Receiver, scope);
        if (_objects.TryGetValue(receiver.Type.Name, out var objectSymbol) && objectSymbol.Syntax.Kind != ObjectDeclarationKind.Interface)
        {
            return EmitObjectMethodCall(call, receiver, objectSymbol, member.Member, scope);
        }

        return EmitCollectionMethodCall(call, member, scope, receiver);
    }

    private ExpressionResult EmitSourcePackageFunctionCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, string packageName, Scope scope)
    {
        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        var module = packageName[(packageName.LastIndexOf('.') + 1)..].Replace("-", "_", StringComparison.Ordinal);
        var functionName = module + "_" + member.Member.Text;
        if (_functions.TryGetValue(functionName, out var function))
        {
            return EmitFunctionCall(call, function, arguments, call.TypeArguments.Select(type => ResolveType(type, EmptyGenericNames, type.Span)).ToArray());
        }

        Report("VEL3005", member.Member.Span, $"Source package '{packageName}' does not export function '{member.Member.Text}'.", $"Declare 'fn {functionName}(...)' in package '{packageName}'.");
        return new ExpressionResult(VelaType.Unknown, $"{EscapeIdentifier(functionName)}({string.Join(", ", arguments.Select(static argument => argument.Code))})");
    }

    private ExpressionResult EmitCoreModuleCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, string module, Scope scope)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, $"Core module function '{member.Member.Text}' does not accept type arguments.");
        }

        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        return module switch
        {
            "vela.core.json" => EmitJsonCall(call, member.Member, arguments),
            "vela.core.crypto" => EmitCryptoCall(call, member.Member, arguments),
            "vela.core.tcp" => EmitTcpCall(call, member.Member, arguments),
            "vela.core.text" => EmitTextCall(call, member.Member, arguments),
            "vela.core.math" => EmitMathCall(call, member.Member, arguments),
            "vela.core.time" => EmitTimeCall(call, member.Member, arguments),
            "vela.core.random" => EmitRandomCall(call, member.Member, arguments),
            "vela.core.io" => EmitIoCall(call, member.Member, arguments),
            "vela.core.encoding" => EmitEncodingCall(call, member.Member, arguments),
            "vela.core.env" => EmitEnvironmentCall(call, member.Member, arguments),
            "vela.core.system" => EmitSystemCall(call, member.Member, arguments),
            "vela.core.console" => EmitConsoleCall(call, member.Member, arguments),
            "vela.core.gui" => EmitGuiCall(call, member.Member, arguments),
            "vela.core.http" => EmitHttpCall(call, member.Member, arguments),
            "vela.core.graphql" => EmitGraphqlCall(call, member.Member, arguments),
            "vela.core.grpc" => EmitGrpcCall(call, member.Member, arguments),
            "vela.core.sqlite" => EmitSqliteCall(call, member.Member, arguments),
            "vela.core.postgres" => EmitPostgresCall(call, member.Member, arguments),
            "vela.concurrent" => EmitConcurrentCall(call, member.Member, arguments),
            _ => ReportUnknownCoreOperation(member.Member, module)
        };
    }

    private ExpressionResult EmitSqliteCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "open" => EmitCoreFunction(call, operation, arguments, VelaType.SqliteDatabase, [VelaType.Text], values => $"VelaSqlite.Open({values[0]})"),
        "execute" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Execute({values[0]}, {values[1]})"),
        "query" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Query({values[0]}, {values[1]})"),
        "migrate" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Migrate({values[0]}, {values[1]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.SqliteDatabase], values => $"VelaSqlite.Close({values[0]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.sqlite")
    };

    private ExpressionResult EmitPostgresCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "open" => EmitCoreFunction(call, operation, arguments, VelaType.PostgresDatabase, [VelaType.Text], values => $"VelaPostgres.Open({values[0]})"),
        "execute" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Execute({values[0]}, {values[1]})"),
        "query" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Query({values[0]}, {values[1]})"),
        "migrate" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Migrate({values[0]}, {values[1]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.PostgresDatabase], values => $"VelaPostgres.Close({values[0]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.postgres")
    };

    private ExpressionResult EmitJsonCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "is_valid" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaJson.IsValid({values[0]})"),
        "quote" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Quote({values[0]})"),
        "compact" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Compact({values[0]}, {SourceLocationCode(call)})"),
        "pretty" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Pretty({values[0]}, {SourceLocationCode(call)})"),
        "try_get_text" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "try_get_int" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Int]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetInt({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "try_get_bool" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Bool]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetBool({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.json")
    };

    private ExpressionResult EmitCryptoCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "sha256" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaCrypto.Sha256({values[0]})"),
        "hmac_sha256" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaCrypto.HmacSha256({values[0]}, {values[1]})"),
        "constant_time_equals" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaCrypto.ConstantTimeEquals({values[0]}, {values[1]})"),
        "random_hex" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaCrypto.RandomHex({values[0]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.crypto")
    };

    private ExpressionResult EmitTcpCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "connect" => EmitCoreFunction(call, operation, arguments, VelaType.TcpConnection, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"TcpConnection.Connect({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "connect_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.TcpConnection]), [VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Cancellation], values => $"TcpConnection.ConnectAsync({values[0]}, {values[1]}, {values[2]}, {values[3]}, {SourceLocationCode(call)})"),
        "send_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.TcpConnection, VelaType.Text], values => $"{values[0]}.SendText({values[1]}, {SourceLocationCode(call)})"),
        "send_text_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.Unit]), [VelaType.TcpConnection, VelaType.Text, VelaType.Cancellation], values => $"{values[0]}.SendTextAsync({values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "receive_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.TcpConnection, VelaType.Int], values => $"{values[0]}.ReceiveText({values[1]}, {SourceLocationCode(call)})"),
        "receive_text_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.Text]), [VelaType.TcpConnection, VelaType.Int, VelaType.Cancellation], values => $"{values[0]}.ReceiveTextAsync({values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.TcpConnection], values => $"{values[0]}.Dispose()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.tcp")
    };

    private ExpressionResult EmitTextCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "length" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Text], values => $"VelaTextOperations.Length({values[0]})"),
        "contains" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.Contains({values[0]}, {values[1]})"),
        "starts_with" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.StartsWith({values[0]}, {values[1]})"),
        "ends_with" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.EndsWith({values[0]}, {values[1]})"),
        "trim" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.Trim({values[0]})"),
        "to_upper_invariant" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.ToUpperInvariant({values[0]})"),
        "to_lower_invariant" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.ToLowerInvariant({values[0]})"),
        "from_int" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaTextOperations.FromInt({values[0]})"),
        "from_long" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Long], values => $"VelaTextOperations.FromLong({values[0]})"),
        "from_double" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Double], values => $"VelaTextOperations.FromDouble({values[0]})"),
        "from_bool" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Bool], values => $"VelaTextOperations.FromBool({values[0]})"),
        "try_parse_int" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Int]), [VelaType.Text], values => $"VelaTextOperations.TryParseInt({values[0]})"),
        "try_parse_long" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Long]), [VelaType.Text], values => $"VelaTextOperations.TryParseLong({values[0]})"),
        "try_parse_double" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Double]), [VelaType.Text], values => $"VelaTextOperations.TryParseDouble({values[0]})"),
        "try_parse_bool" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Bool]), [VelaType.Text], values => $"VelaTextOperations.TryParseBool({values[0]})"),
        "from_code_point" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaTextOperations.FromCodePoint({values[0]}, {SourceLocationCode(call)})"),
        "slice" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaTextOperations.Slice({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.text")
    };

    private ExpressionResult EmitMathCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments)
    {
        if (operation.Text is "sqrt" or "pow")
        {
            var count = operation.Text == "sqrt" ? 1 : 2;
            if (arguments.Length != count)
            {
                Report("VEL3006", call.Span, $"Core function 'math.{operation.Text}' expects {count} argument(s), but received {arguments.Length}.");
            }

            var values = Enumerable.Range(0, count).Select(index => index < arguments.Length ? arguments[index] : new ExpressionResult(VelaType.Double, "0d")).ToArray();
            foreach (var value in values)
            {
                if (!value.Type.IsNumeric)
                {
                    Report("VEL3002", call.Span, $"Core function 'math.{operation.Text}' requires numeric arguments.");
                }
            }

            var numbers = values.Select(value => CoerceNumeric(VelaType.Double, value, call)).ToArray();
            return operation.Text == "sqrt"
                ? new ExpressionResult(VelaType.Double, $"VelaMath.Sqrt({numbers[0]}, {SourceLocationCode(call)})")
                : new ExpressionResult(VelaType.Double, $"VelaMath.Pow({numbers[0]}, {numbers[1]}, {SourceLocationCode(call)})");
        }

        var expectedCount = operation.Text == "abs" ? 1 : operation.Text is "min" or "max" ? 2 : operation.Text == "clamp" ? 3 : 0;
        if (expectedCount == 0)
        {
            return ReportUnknownCoreOperation(operation, "vela.core.math");
        }

        if (arguments.Length != expectedCount)
        {
            Report("VEL3006", call.Span, $"Core function 'math.{operation.Text}' expects {expectedCount} argument(s), but received {arguments.Length}.");
        }

        var resultType = arguments.Length == 0 ? VelaType.Int : arguments[0].Type;
        if (!resultType.IsNumeric)
        {
            Report("VEL3002", call.Span, $"Core function 'math.{operation.Text}' requires numeric arguments.");
        }

        var numericValues = Enumerable.Range(0, expectedCount).Select(index => index < arguments.Length ? arguments[index] : new ExpressionResult(resultType, DefaultValue(resultType))).ToArray();
        foreach (var value in numericValues)
        {
            EnsureAssignable(resultType, value.Type, call.Span);
        }

        var codes = numericValues.Select(value => CoerceCode(resultType, value, call)).ToArray();
        var method = operation.Text switch
        {
            "abs" => "Abs",
            "min" => "Min",
            "max" => "Max",
            _ => "Clamp"
        };
        var location = method == "Abs" && resultType.Name is "Int" or "Long"
            ? ", " + SourceLocationCode(call)
            : string.Empty;
        return new ExpressionResult(resultType, $"VelaMath.{method}({string.Join(", ", codes)}{location})");
    }

    private ExpressionResult EmitTimeCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "utc_unix_milliseconds" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [], _ => "VelaTime.UtcUnixMilliseconds()"),
        "monotonic_ticks" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [], _ => "VelaTime.MonotonicTicks()"),
        "elapsed_milliseconds" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [VelaType.Long, VelaType.Long], values => $"VelaTime.ElapsedMilliseconds({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.time")
    };

    private ExpressionResult EmitRandomCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "next_int" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Int, VelaType.Int], values => $"VelaRandom.NextInt({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "next_double" => EmitCoreFunction(call, operation, arguments, VelaType.Double, [], _ => "VelaRandom.NextDouble()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.random")
    };

    private ExpressionResult EmitIoCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "exists" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaIo.Exists({values[0]})"),
        "directory_exists" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaIo.DirectoryExists({values[0]})"),
        "read_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.ReadText({values[0]}, {SourceLocationCode(call)})"),
        "write_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaIo.WriteText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "append_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaIo.AppendText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "read_lines" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ReadLines({values[0]}, {SourceLocationCode(call)})"),
        "write_lines" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, new VelaType("List", [VelaType.Text])], values => $"VelaIo.WriteLines({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "delete_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaIo.DeleteFile({values[0]}, {SourceLocationCode(call)})"),
        "copy_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text, VelaType.Bool], values => $"VelaIo.CopyFile({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "move_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text, VelaType.Bool], values => $"VelaIo.MoveFile({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "file_size" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [VelaType.Text], values => $"VelaIo.FileSize({values[0]}, {SourceLocationCode(call)})"),
        "create_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaIo.CreateDirectory({values[0]}, {SourceLocationCode(call)})"),
        "delete_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Bool], values => $"VelaIo.DeleteDirectory({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "list_files" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ListFiles({values[0]}, {SourceLocationCode(call)})"),
        "list_directories" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ListDirectories({values[0]}, {SourceLocationCode(call)})"),
        "combine" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaIo.Combine({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "file_name" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.FileName({values[0]}, {SourceLocationCode(call)})"),
        "extension" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.Extension({values[0]}, {SourceLocationCode(call)})"),
        "full_path" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.FullPath({values[0]}, {SourceLocationCode(call)})"),
        "temporary_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => $"VelaIo.TemporaryFile({SourceLocationCode(call)})"),
        "temporary_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => $"VelaIo.TemporaryDirectory({SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.io")
    };

    private ExpressionResult EmitSystemCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "exec" => EmitSystemExec(call, operation, arguments),
        "which" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text], values => $"VelaSystem.Which({values[0]})"),
        "process_id" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [], _ => "VelaSystem.ProcessId()"),
        "temporary_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => "VelaSystem.TemporaryDirectory()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.system")
    };

    private ExpressionResult EmitSystemExec(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments)
    {
        var values = BindNamedCoreArguments(call, operation, arguments, SystemExecParameters);
        return new ExpressionResult(VelaType.ProcessResult, $"VelaSystem.Exec({values[0]}, {values[1]}, {values[2]}, {values[3]}, {SourceLocationCode(call)})");
    }

    private ExpressionResult EmitConsoleCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "write" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.Write({values[0]})"),
        "write_line" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteLine({values[0]})"),
        "write_error" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteError({values[0]})"),
        "write_error_line" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteErrorLine({values[0]})"),
        "is_output_redirected" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [], _ => "VelaConsole.IsOutputRedirected()"),
        "supports_color" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [], _ => "VelaConsole.SupportsColor()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.console")
    };

    private ExpressionResult EmitGuiCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "show_message" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaGui.ShowMessage({values[0]}, {values[1]})"),
        "prompt" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text, VelaType.Text], values => $"VelaGui.Prompt({values[0]}, {values[1]}, {values[2]})"),
        "run_hello_form" => EmitCoreFunction(
            call,
            operation,
            arguments,
            VelaType.Int,
            [VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Text],
            values => $"VelaGui.RunHelloForm({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "run_counter_app" => EmitCoreFunction(
            call,
            operation,
            arguments,
            VelaType.Int,
            [VelaType.Text, VelaType.Text, VelaType.Int],
            values => $"VelaGui.RunCounterApp({values[0]}, {values[1]}, {values[2]})"),
        "create_form" => EmitCoreFunction(call, operation, arguments, VelaType.GuiForm, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.CreateForm({values[0]}, {values[1]}, {values[2]})"),
        "add_label" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddLabel({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_button" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddButton({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "add_textbox" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddTextBox({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "show" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.Show({values[0]})"),
        "show_owned" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm, VelaType.GuiForm], values => $"VelaGuiComponents.ShowOwned({values[0]}, {values[1]})"),
        "process_events" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.ProcessEvents({values[0]})"),
        "is_open" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiForm], values => $"VelaGuiComponents.IsOpen({values[0]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.Close({values[0]})"),
        "set_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.SetText({values[0]}, {values[1]})"),
        "get_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.GetText({values[0]})"),
        "was_clicked" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiControl], values => $"VelaGuiComponents.WasClicked({values[0]})"),
        "on_click" => EmitGuiCallback(call, arguments, "on_click", CreateFunctionType([], VelaType.Unit), "OnClick"),
        "on_text_changed" => EmitGuiCallback(call, arguments, "on_text_changed", CreateFunctionType([VelaType.Text], VelaType.Unit), "OnTextChanged"),
        "on_checked_changed" => EmitGuiCallback(call, arguments, "on_checked_changed", CreateFunctionType([VelaType.Bool], VelaType.Unit), "OnCheckedChanged"),
        "on_value_changed" => EmitGuiCallback(call, arguments, "on_value_changed", CreateFunctionType([VelaType.Int], VelaType.Unit), "OnValueChanged"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GuiForm], values => $"VelaGuiComponents.Run({values[0]})"),
        "create_form_layout" => EmitCoreFunction(call, operation, arguments, VelaType.GuiForm, [VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Text], values => $"VelaGuiComponents.CreateFormLayout({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_checkbox" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddCheckBox({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_progress" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddProgress({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_combo" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddComboBox({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_list" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddList({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_grid" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddGrid({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_slider" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddSlider({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]}, {values[6]}, {values[7]})"),
        "add_textarea" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddTextArea({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "add_numeric" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddNumeric({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]}, {values[6]}, {values[7]})"),
        "add_radio" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddRadio({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_separator" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddSeparator({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "get_value" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GuiControl], values => $"VelaGuiComponents.GetValue({values[0]})"),
        "set_value" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Int], values => $"VelaGuiComponents.SetValue({values[0]}, {values[1]})"),
        "add_menu_item" => EmitGuiCallback(call, arguments, "add_menu_item", CreateFunctionType([], VelaType.Unit), "AddMenuItem", formFirst: true),
        "open_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiForm, VelaType.Text], values => $"VelaGuiComponents.OpenFile({values[0]}, {values[1]})"),
        "save_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiForm, VelaType.Text, VelaType.Text], values => $"VelaGuiComponents.SaveFile({values[0]}, {values[1]}, {values[2]})"),
        "combo_add" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.ComboAdd({values[0]}, {values[1]})"),
        "combo_selected" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.ComboSelected({values[0]})"),
        "list_add" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.ListAdd({values[0]}, {values[1]})"),
        "list_clear" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl], values => $"VelaGuiComponents.ListClear({values[0]})"),
        "list_selected" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.ListSelected({values[0]})"),
        "grid_add_row" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.GridAddRow({values[0]}, {values[1]})"),
        "grid_clear" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl], values => $"VelaGuiComponents.GridClear({values[0]})"),
        "is_checked" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiControl], values => $"VelaGuiComponents.IsChecked({values[0]})"),
        "set_checked" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetChecked({values[0]}, {values[1]})"),
        "set_progress" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Int], values => $"VelaGuiComponents.SetProgress({values[0]}, {values[1]})"),
        "set_enabled" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetEnabled({values[0]}, {values[1]})"),
        "set_visible" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetVisible({values[0]}, {values[1]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.gui")
    };

    private ExpressionResult EmitHttpCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_server" => EmitCoreFunction(call, operation, arguments, VelaType.HttpServer, [VelaType.Text, VelaType.Int], values => $"VelaHttp.CreateServer({values[0]}, {values[1]})"),
        "get" => EmitHttpRouteCallback(call, arguments, "get", CreateFunctionType([], VelaType.Text), "Get"),
        "post" => EmitHttpRouteCallback(call, arguments, "post", CreateFunctionType([VelaType.Text], VelaType.Text), "Post"),
        "put" => EmitHttpRouteCallback(call, arguments, "put", CreateFunctionType([VelaType.Text], VelaType.Text), "Put"),
        "delete" => EmitHttpRouteCallback(call, arguments, "delete", CreateFunctionType([], VelaType.Text), "Delete"),
        "start" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.HttpServer], values => $"VelaHttp.Start({values[0]})"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.HttpServer], values => $"VelaHttp.Run({values[0]})"),
        "stop" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer], values => $"VelaHttp.Stop({values[0]})"),
        "set_max_body" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer, VelaType.Int], values => $"VelaHttp.SetMaxBody({values[0]}, {values[1]})"),
        "client_get" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text], values => $"VelaHttp.ClientGet({values[0]}, {values[1]}, {values[2]})"),
        "client_post" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text, VelaType.Text], values => $"VelaHttp.ClientPost({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "request_get" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int], values => $"VelaHttpClient.Get({values[0]}, {values[1]})"),
        "request_post" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Int], values => $"VelaHttpClient.Post({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.http")
    };

    private ExpressionResult EmitGraphqlCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_schema" => EmitCoreFunction(call, operation, arguments, VelaType.GraphqlSchema, [], _ => "VelaGraphql.CreateSchema()"),
        "query" => EmitGraphqlFieldCallback(call, arguments, "query", CreateFunctionType([], VelaType.Text), "Query"),
        "query_args" => EmitGraphqlFieldCallback(call, arguments, "query_args", CreateFunctionType([VelaType.Text], VelaType.Text), "QueryArgs"),
        "mutation" => EmitGraphqlFieldCallback(call, arguments, "mutation", CreateFunctionType([VelaType.Text], VelaType.Text), "Mutation"),
        "mount" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer, VelaType.Text, VelaType.GraphqlSchema], values => $"VelaGraphql.Mount({values[0]}, {values[1]}, {values[2]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.graphql")
    };

    private ExpressionResult EmitGrpcCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_server" => EmitCoreFunction(call, operation, arguments, VelaType.GrpcServer, [VelaType.Text, VelaType.Int], values => $"VelaGrpc.CreateServer({values[0]}, {values[1]})"),
        "map" => EmitGrpcMapCallback(call, arguments),
        "start" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GrpcServer], values => $"VelaGrpc.Start({values[0]})"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GrpcServer], values => $"VelaGrpc.Run({values[0]})"),
        "stop" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GrpcServer], values => $"VelaGrpc.Stop({values[0]})"),
        "client_call" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text, VelaType.Text], values => $"VelaGrpc.ClientCall({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.grpc")
    };

    private ExpressionResult EmitHttpRouteCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod)
    {
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'http.{operationName}' expects 3 argument(s), but received {arguments.Length}.");
        }

        var server = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.HttpServer, "default!");
        var path = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!server.Type.IsSameAs(VelaType.HttpServer) && !server.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects HttpServer for argument 1.");
        }

        if (!path.Type.IsSameAs(VelaType.Text) && !path.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaHttp.{runtimeMethod}({server.Code}, {path.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGraphqlFieldCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod)
    {
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'graphql.{operationName}' expects 3 argument(s), but received {arguments.Length}.");
        }

        var schema = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GraphqlSchema, "default!");
        var name = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!schema.Type.IsSameAs(VelaType.GraphqlSchema) && !schema.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects GraphqlSchema for argument 1.");
        }

        if (!name.Type.IsSameAs(VelaType.Text) && !name.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGraphql.{runtimeMethod}({schema.Code}, {name.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGrpcMapCallback(CallExpressionSyntax call, ExpressionResult[] arguments)
    {
        var expectedHandler = CreateFunctionType([VelaType.Text], VelaType.Text);
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'grpc.map' expects 3 argument(s), but received {arguments.Length}.");
        }

        var server = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GrpcServer, "default!");
        var method = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!server.Type.IsSameAs(VelaType.GrpcServer) && !server.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, "Core function 'grpc.map' expects GrpcServer for argument 1.");
        }

        if (!method.Type.IsSameAs(VelaType.Text) && !method.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, "Core function 'grpc.map' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'grpc.map' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGrpc.Map({server.Code}, {method.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGuiCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod,
        bool formFirst = false)
    {
        if (arguments.Length != 2 && !(formFirst && arguments.Length == 3))
        {
            var expected = formFirst ? 3 : 2;
            Report("VEL3006", call.Span, $"Core function 'gui.{operationName}' expects {expected} argument(s), but received {arguments.Length}.");
        }

        if (formFirst)
        {
            var form = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GuiForm, "default!");
            var path = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
            var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
            if (!form.Type.IsSameAs(VelaType.GuiForm) && !form.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects GuiForm for argument 1.");
            }

            if (!path.Type.IsSameAs(VelaType.Text) && !path.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects Text for argument 2.");
            }

            if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
            }

            return new ExpressionResult(VelaType.Unit, $"VelaGuiComponents.{runtimeMethod}({form.Code}, {path.Code}, {handler.Code})");
        }

        var control = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GuiControl, "default!");
        var callback = arguments.Length > 1 ? arguments[1] : new ExpressionResult(expectedHandler, "null!");
        if (!control.Type.IsSameAs(VelaType.GuiControl) && !control.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects GuiControl for argument 1, but received '{control.Type}'.");
        }

        if (!callback.Type.IsSameAs(expectedHandler) && !callback.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects {expectedHandler}, but received '{callback.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGuiComponents.{runtimeMethod}({control.Code}, {callback.Code})");
    }

    private ExpressionResult EmitEncodingCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "utf8_byte_count" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Text], values => $"VelaEncoding.Utf8ByteCount({values[0]})"),
        "hex_encode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.HexEncode({values[0]})"),
        "base64_encode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.Base64Encode({values[0]})"),
        "hex_decode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.HexDecode({values[0]}, {SourceLocationCode(call)})"),
        "base64_decode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.Base64Decode({values[0]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.encoding")
    };

    private ExpressionResult EmitEnvironmentCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "get" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text], values => $"VelaEnvironment.Get({values[0]})"),
        "get_or" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaEnvironment.GetOr({values[0]}, {values[1]})"),
        "argument_count" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [], _ => "VelaEnvironment.ArgumentCount()"),
        "argument" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Int], values => $"VelaEnvironment.Argument({values[0]})"),
        "current_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => "VelaEnvironment.CurrentDirectory()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.env")
    };

    private ExpressionResult EmitConcurrentCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create" => EmitCoreFunction(call, operation, arguments, VelaType.Cancellation, [], _ => "VelaCancellation.Create()"),
        "cancel" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Cancellation], values => $"{values[0]}.Cancel()"),
        "is_cancelled" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Cancellation], values => $"{values[0]}.IsCancellationRequested"),
        _ => ReportUnknownCoreOperation(operation, "vela.concurrent")
    };

    private ExpressionResult EmitCoreFunction(
        CallExpressionSyntax call,
        SyntaxToken operation,
        ExpressionResult[] arguments,
        VelaType returnType,
        IReadOnlyList<VelaType> parameterTypes,
        Func<string[], string> emit)
    {
        foreach (var argument in call.Arguments.Where(static argument => argument.IsNamed))
        {
            Report("VEL3023", argument.Name!.Span, $"Core function '{operation.Text}' does not expose named arguments.", "Pass these intrinsic arguments positionally; system.exec supports named limits.");
        }

        if (arguments.Length != parameterTypes.Count)
        {
            Report("VEL3006", call.Span, $"Core function '{operation.Text}' expects {parameterTypes.Count} argument(s), but received {arguments.Length}.");
        }

        var values = new string[parameterTypes.Count];
        for (var index = 0; index < parameterTypes.Count; index++)
        {
            var argument = index < arguments.Length ? arguments[index] : new ExpressionResult(parameterTypes[index], DefaultValue(parameterTypes[index]));
            EnsureAssignable(parameterTypes[index], argument.Type, index < call.Arguments.Count ? call.Arguments[index].Span : call.Span);
            values[index] = CoerceCode(parameterTypes[index], argument, call);
        }

        return new ExpressionResult(returnType, emit(values));
    }

    private string[] BindNamedCoreArguments(
        CallExpressionSyntax call,
        SyntaxToken operation,
        ExpressionResult[] suppliedValues,
        IReadOnlyList<CoreParameter> parameters)
    {
        var values = new ExpressionResult?[parameters.Count];
        var sources = new SyntaxNode?[parameters.Count];
        var nextPositional = 0;
        for (var sourceIndex = 0; sourceIndex < call.Arguments.Count; sourceIndex++)
        {
            var argument = call.Arguments[sourceIndex];
            var parameterIndex = argument.Name is null
                ? nextPositional++
                : parameters.Select(static (parameter, index) => (parameter, index))
                    .Where(pair => string.Equals(pair.parameter.Name, argument.Name.Text, StringComparison.Ordinal))
                    .Select(static pair => pair.index)
                    .DefaultIfEmpty(-1)
                    .First();
            if (parameterIndex < 0 || parameterIndex >= parameters.Count)
            {
                Report("VEL3023", argument.Span, argument.Name is null
                    ? $"Core function '{operation.Text}' received too many arguments."
                    : $"Core function '{operation.Text}' has no parameter named '{argument.Name.Text}'.");
                continue;
            }

            if (values[parameterIndex] is not null)
            {
                Report("VEL3023", argument.Span, $"Core parameter '{parameters[parameterIndex].Name}' is supplied more than once.", "Pass each parameter only once.");
                continue;
            }

            values[parameterIndex] = sourceIndex < suppliedValues.Length ? suppliedValues[sourceIndex] : new ExpressionResult(VelaType.Unknown, "default");
            sources[parameterIndex] = argument.Expression;
        }

        var codes = new string[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (values[index] is null)
            {
                if (parameter.DefaultCode is null)
                {
                    Report("VEL3023", call.Span, $"Required parameter '{parameter.Name}' is missing for core function '{operation.Text}'.", "Supply every required process argument.");
                    values[index] = new ExpressionResult(parameter.Type, DefaultValue(parameter.Type));
                }
                else
                {
                    values[index] = new ExpressionResult(parameter.Type, parameter.DefaultCode);
                }

                sources[index] = call;
            }

            EnsureAssignable(parameter.Type, values[index]!.Type, sources[index]!.Span);
            codes[index] = CoerceCode(parameter.Type, values[index]!, sources[index]!);
        }

        return codes;
    }

    private ExpressionResult ReportUnknownCoreOperation(SyntaxToken operation, string module)
    {
        Report("VEL3005", operation.Span, $"Core module '{module}' does not export '{operation.Text}'.", "Use a documented core module operation.");
        return new ExpressionResult(VelaType.Unknown, "default");
    }

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

    private ExpressionResult EmitCollectionMethodCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, Scope scope, ExpressionResult? knownReceiver = null)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, "Collection methods do not accept explicit type arguments.");
        }

        var receiver = knownReceiver ?? EmitExpression(member.Receiver, scope);
        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        return receiver.Type.Name switch
        {
            "List" when receiver.Type.TypeArguments.Count == 1 => EmitVectorMethod(call, receiver, member.Member, arguments),
            "HashMap" when receiver.Type.TypeArguments.Count == 2 => EmitHashMapMethod(call, receiver, member.Member, arguments),
            "HashSet" when receiver.Type.TypeArguments.Count == 1 => EmitHashSetMethod(call, receiver, member.Member, arguments),
            "Queue" when receiver.Type.TypeArguments.Count == 1 => EmitQueueMethod(call, receiver, member.Member, arguments),
            "Stack" when receiver.Type.TypeArguments.Count == 1 => EmitStackMethod(call, receiver, member.Member, arguments),
            "RingBuffer" when receiver.Type.TypeArguments.Count == 1 => EmitRingBufferMethod(call, receiver, member.Member, arguments),
            "BitSet" => EmitBitSetMethod(call, receiver, member.Member, arguments),
            "SortedMap" when receiver.Type.TypeArguments.Count == 2 => EmitSortedMapMethod(call, receiver, member.Member, arguments),
            "SortedSet" when receiver.Type.TypeArguments.Count == 1 => EmitSortedSetMethod(call, receiver, member.Member, arguments),
            "Deque" when receiver.Type.TypeArguments.Count == 1 => EmitDequeMethod(call, receiver, member.Member, arguments),
            "PriorityQueue" when receiver.Type.TypeArguments.Count == 1 => EmitPriorityQueueMethod(call, receiver, member.Member, arguments),
            "LinkedList" when receiver.Type.TypeArguments.Count == 1 => EmitLinkedListMethod(call, receiver, member.Member, arguments),
            "Array" when receiver.Type.TypeArguments.Count == 1 => ReportUnsupportedCollectionMethod(call, receiver, member.Member, arguments),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member.Member, arguments)
        };
    }

    private ExpressionResult EmitObjectMethodCall(CallExpressionSyntax call, ExpressionResult receiver, ObjectSymbol objectSymbol, SyntaxToken member, Scope scope)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, "Object methods do not accept explicit type arguments in this compiler version.");
        }

        var method = objectSymbol.Syntax.Members.OfType<ObjectMethodSyntax>()
            .Select(static candidate => candidate.Function)
            .FirstOrDefault(candidate => candidate.Identifier.Text == member.Text);
        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        if (method is null)
        {
            Report("VEL3009", member.Span, $"Type '{receiver.Type}' does not contain method '{member.Text}'.");
            return new ExpressionResult(VelaType.Unknown, "default");
        }

        ReportAttributeUse(method.Attributes ?? [], member.Span, $"method '{objectSymbol.Syntax.Identifier.Text}.{member.Text}'");

        var bound = BindCallArguments(call, method.Parameters, arguments, objectSymbol.GenericNames, $"method '{member.Text}'");
        arguments = bound.Values;
        var substitutions = CreateGenericSubstitutions(objectSymbol.GenericNames, receiver.Type.TypeArguments.ToArray());

        for (var index = 0; index < arguments.Length; index++)
        {
            var expected = ResolveType(method.Parameters[index].Type, objectSymbol.GenericNames, method.Parameters[index].Type.Span).Substitute(substitutions);
            EnsureAssignable(expected, arguments[index].Type, bound.Sources[index].Span);
        }

        var returnType = ResolveType(method.ReturnType, objectSymbol.GenericNames, method.Identifier.Span, VelaType.Unit, allowVoid: true).Substitute(substitutions);
        var callableReturnType = method.AsyncKeyword is null ? returnType : new VelaType("Future", [returnType]);
        CallEvaluationStep? receiverStep = null;
        var receiverCode = receiver.Code;
        if (bound.EvaluationSteps.Length > 0)
        {
            receiverCode = NextCallArgumentTemporary();
            receiverStep = new CallEvaluationStep(receiverCode, receiver.Code);
        }

        var argumentCodes = bound.EmissionOrder.Select(index =>
        {
            var expected = ResolveType(method.Parameters[index].Type, objectSymbol.GenericNames, method.Parameters[index].Type.Span).Substitute(substitutions);
            var code = CoerceCode(expected, arguments[index], bound.Sources[index]);
            return bound.UseNamedEmission ? $"{EscapeIdentifier(method.Parameters[index].Identifier.Text)}: {code}" : code;
        });
        var code = $"{receiverCode}.{EscapeIdentifier(member.Text)}({string.Join(", ", argumentCodes)})";
        return WrapBoundCall(callableReturnType, code, bound, receiverStep);
    }

    private ExpressionResult EmitVectorMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "append" => EmitCollectionOperation(call, receiver, member, "Append", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitHashMapMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var key = receiver.Type.TypeArguments[0];
        var value = receiver.Type.TypeArguments[1];
        EnsureHashable(key, member.Span);
        return member.Text switch
        {
            "set" => EmitCollectionOperation(call, receiver, member, "Set", VelaType.Unit, arguments, [key, value]),
            "try_get" => EmitCollectionOperation(call, receiver, member, "TryGet", new VelaType("Option", [value]), arguments, [key]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [key]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [key]),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitHashSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureHashable(element, member.Span);
        return member.Text switch
        {
            "add" => EmitCollectionOperation(call, receiver, member, "Add", VelaType.Bool, arguments, [element]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitQueueMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "enqueue" => EmitCollectionOperation(call, receiver, member, "Enqueue", VelaType.Unit, arguments, [element]),
            "dequeue" => EmitCollectionOperation(call, receiver, member, "Dequeue", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitStackMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push" => EmitCollectionOperation(call, receiver, member, "Push", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitRingBufferMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "try_enqueue" => EmitCollectionOperation(call, receiver, member, "TryEnqueue", VelaType.Bool, arguments, [element]),
            "dequeue" => EmitCollectionOperation(call, receiver, member, "Dequeue", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitSortedMapMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var key = receiver.Type.TypeArguments[0];
        var value = receiver.Type.TypeArguments[1];
        EnsureOrdered(key, member.Span);
        return member.Text switch
        {
            "set" => EmitCollectionOperation(call, receiver, member, "Set", VelaType.Unit, arguments, [key, value]),
            "try_get" => EmitCollectionOperation(call, receiver, member, "TryGet", new VelaType("Option", [value]), arguments, [key]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [key]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [key]),
            "first_key" => EmitCollectionOperation(call, receiver, member, "FirstKey", new VelaType("Option", [key]), arguments, []),
            "last_key" => EmitCollectionOperation(call, receiver, member, "LastKey", new VelaType("Option", [key]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitSortedSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureOrdered(element, member.Span);
        return member.Text switch
        {
            "add" => EmitCollectionOperation(call, receiver, member, "Add", VelaType.Bool, arguments, [element]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "first" => EmitCollectionOperation(call, receiver, member, "First", new VelaType("Option", [element]), arguments, []),
            "last" => EmitCollectionOperation(call, receiver, member, "Last", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitDequeMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push_front" => EmitCollectionOperation(call, receiver, member, "PushFront", VelaType.Unit, arguments, [element]),
            "push_back" => EmitCollectionOperation(call, receiver, member, "PushBack", VelaType.Unit, arguments, [element]),
            "pop_front" => EmitCollectionOperation(call, receiver, member, "PopFront", new VelaType("Option", [element]), arguments, []),
            "pop_back" => EmitCollectionOperation(call, receiver, member, "PopBack", new VelaType("Option", [element]), arguments, []),
            "peek_front" => EmitCollectionOperation(call, receiver, member, "PeekFront", new VelaType("Option", [element]), arguments, []),
            "peek_back" => EmitCollectionOperation(call, receiver, member, "PeekBack", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitPriorityQueueMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureOrdered(element, member.Span);
        return member.Text switch
        {
            "push" => EmitCollectionOperation(call, receiver, member, "Push", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitLinkedListMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push_front" => EmitCollectionOperation(call, receiver, member, "PushFront", VelaType.Unit, arguments, [element]),
            "push_back" => EmitCollectionOperation(call, receiver, member, "PushBack", VelaType.Unit, arguments, [element]),
            "pop_front" => EmitCollectionOperation(call, receiver, member, "PopFront", new VelaType("Option", [element]), arguments, []),
            "pop_back" => EmitCollectionOperation(call, receiver, member, "PopBack", new VelaType("Option", [element]), arguments, []),
            "peek_front" => EmitCollectionOperation(call, receiver, member, "PeekFront", new VelaType("Option", [element]), arguments, []),
            "peek_back" => EmitCollectionOperation(call, receiver, member, "PeekBack", new VelaType("Option", [element]), arguments, []),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitBitSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        return member.Text switch
        {
            "set" => EmitBitSetOperation(call, receiver, member, "Set", VelaType.Unit, arguments, expectsIndex: true),
            "clear" when arguments.Length == 0 => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            "clear" => EmitBitSetOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, expectsIndex: true),
            "contains" => EmitBitSetOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, expectsIndex: true),
            "reserve" => EmitBitSetOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, expectsIndex: false),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitBitSetOperation(
        CallExpressionSyntax call,
        ExpressionResult receiver,
        SyntaxToken member,
        string runtimeMember,
        VelaType returnType,
        ExpressionResult[] arguments,
        bool expectsIndex)
    {
        if (arguments.Length != 1)
        {
            var kind = expectsIndex ? "index" : "capacity";
            Report("VEL3006", call.Span, $"Method '{member.Text}' expects exactly one Int {kind} argument.");
        }

        if (arguments.Length > 0)
        {
            EnsureAssignable(VelaType.WholeNumber, arguments[0].Type, call.Arguments[0].Span);
        }

        var argumentCode = arguments.Length == 0 ? "0" : $"checked((int){arguments[0].Code})";
        return new ExpressionResult(returnType, $"{receiver.Code}.{runtimeMember}({argumentCode})");
    }

    private ExpressionResult EmitCollectionOperation(
        CallExpressionSyntax call,
        ExpressionResult receiver,
        SyntaxToken member,
        string runtimeMember,
        VelaType returnType,
        ExpressionResult[] arguments,
        IReadOnlyList<VelaType> parameterTypes)
    {
        if (arguments.Length != parameterTypes.Count)
        {
            Report("VEL3006", call.Span, $"Method '{member.Text}' expects {parameterTypes.Count} argument(s), but received {arguments.Length}.");
        }

        for (var index = 0; index < Math.Min(arguments.Length, parameterTypes.Count); index++)
        {
            EnsureAssignable(parameterTypes[index], arguments[index].Type, call.Arguments[index].Span);
        }

        var argumentsCode = arguments.Select((argument, index) => index < parameterTypes.Count
            ? CoerceCode(parameterTypes[index], argument, call.Arguments[index])
            : argument.Code);
        return new ExpressionResult(returnType, $"{receiver.Code}.{runtimeMember}({string.Join(", ", argumentsCode)})");
    }

    private ExpressionResult ReportUnsupportedCollectionMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        Report("VEL3009", member.Span, $"Type '{receiver.Type}' does not contain method '{member.Text}'.", "Use a supported collection operation or correct the method name.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}.{EscapeIdentifier(member.Text)}({string.Join(", ", arguments.Select(static argument => argument.Code))})");
    }

    private ExpressionResult EmitCollectionConstruction(CallExpressionSyntax call, string name, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        return name switch
        {
            "List" or "Vector" => EmitGenericCollectionConstruction(call, name, "VelaVector", "List", 1, arguments, explicitTypes, capacityRequired: false),
            "HashMap" => EmitGenericCollectionConstruction(call, name, "VelaHashMap", "HashMap", 2, arguments, explicitTypes, capacityRequired: false),
            "HashSet" => EmitGenericCollectionConstruction(call, name, "VelaHashSet", "HashSet", 1, arguments, explicitTypes, capacityRequired: false),
            "Queue" => EmitGenericCollectionConstruction(call, name, "VelaQueue", "Queue", 1, arguments, explicitTypes, capacityRequired: false),
            "Stack" => EmitGenericCollectionConstruction(call, name, "VelaStack", "Stack", 1, arguments, explicitTypes, capacityRequired: false),
            "RingBuffer" => EmitGenericCollectionConstruction(call, name, "RingBuffer", "RingBuffer", 1, arguments, explicitTypes, capacityRequired: true),
            "Array" => EmitGenericCollectionConstruction(call, name, "VelaArray", "Array", 1, arguments, explicitTypes, capacityRequired: true),
            "BitSet" => EmitBitSetConstruction(call, arguments, explicitTypes),
            "SortedMap" => EmitGenericCollectionConstruction(call, name, "VelaSortedMap", "SortedMap", 2, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            "SortedSet" => EmitGenericCollectionConstruction(call, name, "VelaSortedSet", "SortedSet", 1, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            "Deque" => EmitGenericCollectionConstruction(call, name, "VelaDeque", "Deque", 1, arguments, explicitTypes, capacityRequired: false),
            "PriorityQueue" => EmitGenericCollectionConstruction(call, name, "VelaPriorityQueue", "PriorityQueue", 1, arguments, explicitTypes, capacityRequired: false),
            "LinkedList" => EmitGenericCollectionConstruction(call, name, "VelaLinkedList", "LinkedList", 1, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            _ => throw new InvalidOperationException($"Unsupported collection constructor '{name}'.")
        };
    }

    private ExpressionResult EmitGenericCollectionConstruction(
        CallExpressionSyntax call,
        string sourceName,
        string runtimeName,
        string canonicalName,
        int expectedTypeArgumentCount,
        ExpressionResult[] arguments,
        VelaType[] explicitTypes,
        bool capacityRequired,
        bool capacitySupported = true)
    {
        var typeArguments = ValidateCollectionTypeArguments(call, sourceName, explicitTypes, expectedTypeArgumentCount);
        if (canonicalName is "HashMap" or "HashSet")
        {
            EnsureHashable(typeArguments[0], call.Span);
        }

        if (canonicalName is "SortedMap" or "SortedSet" or "PriorityQueue")
        {
            EnsureOrdered(typeArguments[0], call.Span);
        }

        if (!capacitySupported)
        {
            if (arguments.Length != 0)
            {
                Report("VEL3006", call.Span, $"Collection '{sourceName}' does not accept constructor arguments.");
            }

            return new ExpressionResult(new VelaType(canonicalName, typeArguments), $"new {runtimeName}<{string.Join(", ", typeArguments.Select(CSharpType))}>()");
        }

        ValidateCapacityConstructorArguments(call, sourceName, arguments, capacityRequired);
        var capacity = arguments.Length == 0 ? string.Empty : arguments[0].Code;
        var genericSuffix = $"<{string.Join(", ", typeArguments.Select(CSharpType))}>";
        return new ExpressionResult(new VelaType(canonicalName, typeArguments), $"new {runtimeName}{genericSuffix}({capacity})");
    }

    private ExpressionResult EmitBitSetConstruction(CallExpressionSyntax call, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        _ = ValidateCollectionTypeArguments(call, "BitSet", explicitTypes, 0);
        ValidateCapacityConstructorArguments(call, "BitSet", arguments, capacityRequired: true);
        var capacity = arguments.Length == 0 ? "0L" : arguments[0].Code;
        return new ExpressionResult(new VelaType("BitSet"), $"new BitSet(checked((int){capacity}))");
    }

    private VelaType[] ValidateCollectionTypeArguments(CallExpressionSyntax call, string name, VelaType[] explicitTypes, int expectedCount)
    {
        if (explicitTypes.Length != expectedCount)
        {
            Report("VEL3006", call.Span, $"Collection '{name}' expects {expectedCount} type argument(s), but received {explicitTypes.Length}.", "Supply explicit type arguments for the collection element or key and value types.");
        }

        return Enumerable.Range(0, expectedCount)
            .Select(index => index < explicitTypes.Length ? explicitTypes[index] : VelaType.Unknown)
            .ToArray();
    }

    private void ValidateCapacityConstructorArguments(CallExpressionSyntax call, string name, ExpressionResult[] arguments, bool capacityRequired)
    {
        var minimum = capacityRequired ? 1 : 0;
        if (arguments.Length < minimum || arguments.Length > 1)
        {
            var expected = capacityRequired ? "exactly one" : "zero or one";
            Report("VEL3006", call.Span, $"Collection '{name}' expects {expected} Int capacity argument.");
        }

        if (arguments.Length > 0)
        {
            EnsureAssignable(VelaType.WholeNumber, arguments[0].Type, call.Arguments[0].Span);
            if (TryGetIntegerConstant(call.Arguments[0].Expression, out var capacity))
            {
                if (capacity < 0)
                {
                    Report("VEL3006", call.Arguments[0].Span, $"Collection '{name}' capacity cannot be negative.", "Use zero or a positive Int capacity.");
                }
                else if (name == "RingBuffer" && capacity == 0)
                {
                    Report("VEL3006", call.Arguments[0].Span, "RingBuffer capacity must be positive.", "Use an Int capacity greater than zero.");
                }
            }
        }
    }

    private static bool TryGetIntegerConstant(ExpressionSyntax expression, out long value)
    {
        if (expression is LiteralExpressionSyntax { LiteralToken.Kind: TokenKind.IntegerLiteral, LiteralToken.Value: long integer })
        {
            value = integer;
            return true;
        }

        if (expression is UnaryExpressionSyntax
            {
                OperatorToken.Kind: TokenKind.Minus,
                Operand: LiteralExpressionSyntax { LiteralToken.Kind: TokenKind.IntegerLiteral, LiteralToken.Value: long negativeInteger }
            })
        {
            value = -negativeInteger;
            return true;
        }

        value = default;
        return false;
    }

    private BoundCallArguments BindCallArguments(
        CallExpressionSyntax call,
        IReadOnlyList<ParameterSyntax> parameters,
        ExpressionResult[] suppliedValues,
        IReadOnlyCollection<string> genericNames,
        string callableDisplay)
    {
        var values = new ExpressionResult?[parameters.Count];
        var sources = new SyntaxNode?[parameters.Count];
        var emissionOrder = new List<int>(parameters.Count);
        var evaluationSteps = new List<CallEvaluationStep>(parameters.Count);
        var nextPositional = 0;
        var useNamedEmission = call.Arguments.Any(static argument => argument.IsNamed);

        for (var sourceIndex = 0; sourceIndex < call.Arguments.Count; sourceIndex++)
        {
            var argument = call.Arguments[sourceIndex];
            var value = sourceIndex < suppliedValues.Length
                ? suppliedValues[sourceIndex]
                : new ExpressionResult(VelaType.Unknown, "default");
            int parameterIndex;
            if (argument.Name is null)
            {
                parameterIndex = nextPositional++;
                if (parameterIndex >= parameters.Count)
                {
                    Report("VEL3023", argument.Span, $"{callableDisplay} received too many positional arguments.", $"Pass at most {parameters.Count} argument(s).");
                    continue;
                }
            }
            else
            {
                parameterIndex = -1;
                for (var index = 0; index < parameters.Count; index++)
                {
                    if (string.Equals(parameters[index].Identifier.Text, argument.Name.Text, StringComparison.Ordinal))
                    {
                        parameterIndex = index;
                        break;
                    }
                }

                if (parameterIndex < 0)
                {
                    Report("VEL3023", argument.Name.Span, $"{callableDisplay} has no parameter named '{argument.Name.Text}'.", "Use one of the declared parameter names.");
                    continue;
                }
            }

            if (values[parameterIndex] is not null)
            {
                Report("VEL3023", argument.Span, $"Parameter '{parameters[parameterIndex].Identifier.Text}' is supplied more than once to {callableDisplay}.", "Pass each parameter once, either positionally or by name.");
                continue;
            }

            values[parameterIndex] = value;
            sources[parameterIndex] = argument.Expression;
            emissionOrder.Add(parameterIndex);
        }

        var requiresEvaluationWrapper = parameters
            .Select((parameter, index) => (parameter, index))
            .Any(item => values[item.index] is null && item.parameter.DefaultValue is not null);
        if (requiresEvaluationWrapper)
        {
            foreach (var parameterIndex in emissionOrder)
            {
                var value = values[parameterIndex]!;
                var temporary = NextCallArgumentTemporary();
                evaluationSteps.Add(new CallEvaluationStep(temporary, value.Code));
                values[parameterIndex] = new ExpressionResult(value.Type, temporary);
            }
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            if (values[index] is not null)
            {
                continue;
            }

            var parameter = parameters[index];
            useNamedEmission = true;
            if (parameter.DefaultValue is null)
            {
                var parameterType = ResolveType(parameter.Type, genericNames, parameter.Type.Span);
                Report("VEL3023", call.Span, $"Required parameter '{parameter.Identifier.Text}' is missing for {callableDisplay}.", $"Supply '{parameter.Identifier.Text}' with a value of type {parameterType}.");
                values[index] = new ExpressionResult(parameterType, DefaultValue(parameterType));
                sources[index] = call;
                emissionOrder.Add(index);
                continue;
            }

            var defaultScope = new Scope(null);
            for (var previous = 0; previous < index; previous++)
            {
                if (values[previous] is null)
                {
                    continue;
                }

                var previousType = ResolveType(parameters[previous].Type, genericNames, parameters[previous].Type.Span);
                AddVariable(defaultScope, parameters[previous].Identifier.Text, new VariableSymbol(previousType, false, values[previous]!.Code), parameters[previous].Identifier.Span);
            }

            values[index] = EmitExpression(parameter.DefaultValue, defaultScope);
            if (requiresEvaluationWrapper)
            {
                var value = values[index]!;
                var temporary = NextCallArgumentTemporary();
                evaluationSteps.Add(new CallEvaluationStep(temporary, value.Code));
                values[index] = new ExpressionResult(value.Type, temporary);
            }
            sources[index] = parameter.DefaultValue;
            emissionOrder.Add(index);
        }

        return new BoundCallArguments(
            values.Select(static value => value!).ToArray(),
            sources.Select(static source => source!).ToArray(),
            emissionOrder.ToArray(),
            useNamedEmission,
            evaluationSteps.ToArray());
    }

    private ExpressionResult EmitFunctionCall(CallExpressionSyntax call, FunctionSymbol function, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        ReportAttributeUse(function.Syntax.Attributes ?? [], call.Callee.Span, $"function '{function.Syntax.Identifier.Text}'");
        var bound = BindCallArguments(call, function.Syntax.Parameters, arguments, function.GenericNames, $"function '{function.Syntax.Identifier.Text}'");
        arguments = bound.Values;

        var substitutions = CreateGenericSubstitutions(function.GenericNames, explicitTypes);
        if (explicitTypes.Length > 0 && explicitTypes.Length != function.GenericNames.Length)
        {
            Report("VEL3006", call.Span, $"Function '{function.Syntax.Identifier.Text}' expects {function.GenericNames.Length} type argument(s), but received {explicitTypes.Length}.");
        }

        for (var index = 0; index < Math.Min(arguments.Length, function.Syntax.Parameters.Count); index++)
        {
            var expected = ResolveType(function.Syntax.Parameters[index].Type, function.GenericNames, function.Syntax.Parameters[index].Type.Span);
            InferGenericArguments(expected, arguments[index].Type, function.GenericNames, substitutions);
        }

        foreach (var genericName in function.GenericNames)
        {
            if (!substitutions.ContainsKey(genericName))
            {
                Report("VEL3006", call.Span, $"Cannot infer generic type parameter '{genericName}' for function '{function.Syntax.Identifier.Text}'.", "Supply an explicit type argument.");
                substitutions[genericName] = VelaType.Unknown;
            }
        }

        for (var index = 0; index < Math.Min(arguments.Length, function.Syntax.Parameters.Count); index++)
        {
            var expected = ResolveType(function.Syntax.Parameters[index].Type, function.GenericNames, function.Syntax.Parameters[index].Type.Span).Substitute(substitutions);
            EnsureAssignable(expected, arguments[index].Type, bound.Sources[index].Span);
        }

        var returnType = ResolveType(function.Syntax.ReturnType, function.GenericNames, function.Syntax.Identifier.Span, defaultType: VelaType.Unit, allowVoid: true).Substitute(substitutions);
        var callableReturnType = function.Syntax.AsyncKeyword is null ? returnType : new VelaType("Future", [returnType]);
        var typeArguments = explicitTypes.Length > 0
            ? $"<{string.Join(", ", explicitTypes.Select(CSharpType))}>"
            : string.Empty;
        var argumentCodes = bound.EmissionOrder.Select(index =>
        {
            var argument = arguments[index];
            var expected = ResolveType(function.Syntax.Parameters[index].Type, function.GenericNames, function.Syntax.Parameters[index].Type.Span).Substitute(substitutions);
            var code = CoerceCode(expected, argument, bound.Sources[index]);
            return bound.UseNamedEmission ? $"{EscapeIdentifier(function.Syntax.Parameters[index].Identifier.Text)}: {code}" : code;
        });
        var qualifier = _currentObject is null ? string.Empty : "Program.";
        var code = $"{qualifier}{EscapeIdentifier(function.Syntax.Identifier.Text)}{typeArguments}({string.Join(", ", argumentCodes)})";
        return WrapBoundCall(callableReturnType, code, bound);
    }

    private ExpressionResult EmitRecordConstruction(CallExpressionSyntax call, RecordSymbol record, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        ReportAttributeUse(record.Syntax.Attributes ?? [], call.Callee.Span, $"record '{record.Syntax.Identifier.Text}'");
        var fields = record.Syntax.Members.OfType<RecordFieldSyntax>().ToArray();
        var parameters = fields.Select(static field => new ParameterSyntax(field.Identifier, field.ColonToken, field.Type)).ToArray();
        var bound = BindCallArguments(call, parameters, arguments, record.GenericNames, $"record '{record.Syntax.Identifier.Text}'");
        arguments = bound.Values;

        var substitutions = CreateGenericSubstitutions(record.GenericNames, explicitTypes);
        for (var index = 0; index < Math.Min(arguments.Length, fields.Length); index++)
        {
            var expected = ResolveType(fields[index].Type, record.GenericNames, fields[index].Type.Span);
            InferGenericArguments(expected, arguments[index].Type, record.GenericNames, substitutions);
        }

        foreach (var genericName in record.GenericNames)
        {
            if (!substitutions.ContainsKey(genericName))
            {
                Report("VEL3006", call.Span, $"Cannot infer generic type parameter '{genericName}' for record '{record.Syntax.Identifier.Text}'.", "Supply an explicit type argument.");
                substitutions[genericName] = VelaType.Unknown;
            }
        }

        for (var index = 0; index < Math.Min(arguments.Length, fields.Length); index++)
        {
            var expected = ResolveType(fields[index].Type, record.GenericNames, fields[index].Type.Span).Substitute(substitutions);
            EnsureAssignable(expected, arguments[index].Type, bound.Sources[index].Span);
        }

        var constructedType = new VelaType(record.Syntax.Identifier.Text, record.GenericNames.Select(name => substitutions.TryGetValue(name, out var type) ? type : VelaType.Unknown).ToArray());
        var typeArguments = constructedType.TypeArguments.Count == 0 ? string.Empty : $"<{string.Join(", ", constructedType.TypeArguments.Select(CSharpType))}>";
        var argumentCodes = bound.EmissionOrder.Select(index =>
        {
            var argument = arguments[index];
            var expected = ResolveType(fields[index].Type, record.GenericNames, fields[index].Type.Span).Substitute(substitutions);
            var code = CoerceCode(expected, argument, bound.Sources[index]);
            return bound.UseNamedEmission ? $"{EscapeIdentifier(fields[index].Identifier.Text)}: {code}" : code;
        });
        var code = $"new {EscapeIdentifier(record.Syntax.Identifier.Text)}{typeArguments}({string.Join(", ", argumentCodes)})";
        return WrapBoundCall(constructedType, code, bound);
    }

    private ExpressionResult EmitObjectConstruction(CallExpressionSyntax call, ObjectSymbol symbol, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        ReportAttributeUse(symbol.Syntax.Attributes ?? [], call.Callee.Span, $"{symbol.Syntax.Kind.ToString().ToLowerInvariant()} '{symbol.Syntax.Identifier.Text}'");
        var fields = symbol.Syntax.Members.OfType<ObjectFieldSyntax>().ToArray();
        IReadOnlyList<ParameterSyntax> parameters = symbol.Syntax.Kind == ObjectDeclarationKind.Class
            ? symbol.Syntax.ConstructorParameters
            : fields.Select(static field => new ParameterSyntax(field.Identifier, field.ColonToken, field.Type)).ToArray();
        var bound = BindCallArguments(call, parameters, arguments, symbol.GenericNames, $"{symbol.Syntax.Kind.ToString().ToLowerInvariant()} '{symbol.Syntax.Identifier.Text}'");
        arguments = bound.Values;

        var substitutions = CreateGenericSubstitutions(symbol.GenericNames, explicitTypes);
        for (var index = 0; index < arguments.Length; index++)
        {
            var expected = ResolveType(parameters[index].Type, symbol.GenericNames, parameters[index].Type.Span);
            InferGenericArguments(expected, arguments[index].Type, symbol.GenericNames, substitutions);
        }

        foreach (var genericName in symbol.GenericNames)
        {
            if (!substitutions.ContainsKey(genericName))
            {
                Report("VEL3006", call.Span, $"Cannot infer generic type parameter '{genericName}' for {symbol.Syntax.Kind} '{symbol.Syntax.Identifier.Text}'.", "Supply an explicit type argument.");
                substitutions[genericName] = VelaType.Unknown;
            }
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            var expected = ResolveType(parameters[index].Type, symbol.GenericNames, parameters[index].Type.Span).Substitute(substitutions);
            EnsureAssignable(expected, arguments[index].Type, bound.Sources[index].Span);
        }

        var typeArguments = symbol.GenericNames.Select(name => substitutions.TryGetValue(name, out var type) ? type : VelaType.Unknown).ToArray();
        var constructedType = new VelaType(symbol.Syntax.Identifier.Text, typeArguments);
        var genericSuffix = typeArguments.Length == 0 ? string.Empty : $"<{string.Join(", ", typeArguments.Select(CSharpType))}>";
        var argumentCodes = bound.EmissionOrder.Select(index =>
        {
            var argument = arguments[index];
            var expected = ResolveType(parameters[index].Type, symbol.GenericNames, parameters[index].Type.Span).Substitute(substitutions);
            var code = CoerceCode(expected, argument, bound.Sources[index]);
            return bound.UseNamedEmission ? $"{EscapeIdentifier(parameters[index].Identifier.Text)}: {code}" : code;
        });
        var code = $"new {EscapeIdentifier(symbol.Syntax.Identifier.Text)}{genericSuffix}({string.Join(", ", argumentCodes)})";
        return WrapBoundCall(constructedType, code, bound);
    }

    private static ExpressionResult WrapBoundCall(
        VelaType resultType,
        string callCode,
        BoundCallArguments bound,
        CallEvaluationStep? prefix = null)
    {
        if (bound.EvaluationSteps.Length == 0)
        {
            return new ExpressionResult(resultType, callCode);
        }

        var steps = prefix is null ? bound.EvaluationSteps : [prefix, .. bound.EvaluationSteps];
        var declarations = string.Join(" ", steps.Select(static step => $"var {step.Name} = {step.Code};"));
        var code = resultType.IsSameAs(VelaType.Unit)
            ? $"((Action)(() => {{ {declarations} {callCode}; }}))()"
            : $"((Func<{CSharpType(resultType)}>)(() => {{ {declarations} return {callCode}; }}))()";
        return new ExpressionResult(resultType, code);
    }

    private string NextCallArgumentTemporary() => "__velaCallArg" + (_callArgumentIdentifier++).ToString(CultureInfo.InvariantCulture);

    private ExpressionResult EmitList(ListExpressionSyntax list, Scope scope)
    {
        var elements = list.Elements.Select(element => EmitExpression(element, scope)).ToArray();
        var elementType = elements.Length == 0 ? VelaType.Unknown : elements[0].Type;
        foreach (var element in elements.Skip(1))
        {
            EnsureAssignable(elementType, element.Type, list.Span);
        }

        if (elementType.IsUnknown)
        {
            Report("VEL3006", list.Span, "Cannot infer the type of an empty list.", "Add at least one element or use an explicitly typed variable.");
        }

        return new ExpressionResult(
            new VelaType("List", [elementType]),
            $"new VelaVector<{CSharpType(elementType)}>(new {CSharpType(elementType)}[] {{ {string.Join(", ", elements.Select(static element => element.Code))} }})");
    }

    private ExpressionResult UnsupportedExpression(ExpressionSyntax expression)
    {
        Report("VEL3013", expression.Span, "Unsupported expression.");
        return new ExpressionResult(VelaType.Unknown, "default");
    }

    private VelaType ResolveType(
        TypeSyntax? syntax,
        IReadOnlyCollection<string> genericNames,
        TextSpan span,
        VelaType? defaultType = null,
        bool allowVoid = false)
    {
        if (syntax is null)
        {
            return defaultType ?? VelaType.Unknown;
        }

        if (syntax is TupleTypeSyntax tuple)
        {
            var tupleElements = tuple.Elements.Select(element => ResolveType(element, genericNames, element.Span)).ToArray();
            return new VelaType("Tuple", tupleElements);
        }

        if (syntax is FunctionTypeSyntax functionType)
        {
            var parameterTypes = functionType.ParameterTypes
                .Select(parameter => ResolveType(parameter, genericNames, parameter.Span))
                .ToArray();
            var returnType = ResolveType(functionType.ReturnType, genericNames, functionType.ReturnType.Span, allowVoid: true);
            return CreateFunctionType(parameterTypes, returnType);
        }

        if (syntax is not NamedTypeSyntax named)
        {
            Report("VEL3013", syntax.Span, "Unsupported type syntax.");
            return VelaType.Unknown;
        }

        var arguments = named.TypeArguments.Select(argument => ResolveType(argument, genericNames, argument.Span)).ToArray();
        var type = named.Identifier.Text switch
        {
            "Int" => VelaType.Int,
            "UInt" => VelaType.UInt,
            "Long" => VelaType.Long,
            "Float" => VelaType.Float,
            "Double" => VelaType.Double,
            "Decimal" => VelaType.Decimal,
            "Bool" => VelaType.Bool,
            "Text" or "String" => VelaType.Text,
            "Any" => VelaType.Any,
            "TcpConnection" => VelaType.TcpConnection,
            "GuiForm" => VelaType.GuiForm,
            "GuiControl" => VelaType.GuiControl,
            "HttpServer" => VelaType.HttpServer,
            "GraphqlSchema" => VelaType.GraphqlSchema,
            "GrpcServer" => VelaType.GrpcServer,
            "SqliteDatabase" => VelaType.SqliteDatabase,
            "PostgresDatabase" => VelaType.PostgresDatabase,
            "Cancellation" => VelaType.Cancellation,
            "ProcessResult" => VelaType.ProcessResult,
            "Void" or "Unit" when allowVoid => VelaType.Unit,
            "Void" or "Unit" => ReportVoidValueType(named),
            "List" or "Vector" => new VelaType("List", arguments),
            "Array" or "Result" or "Option" or "Future" or "HashMap" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "BitSet" or "SortedMap" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList" => new VelaType(named.Identifier.Text, arguments),
            _ when RuntimeExceptionRanks.ContainsKey(named.Identifier.Text) && arguments.Length == 0 => new VelaType(named.Identifier.Text),
            _ when genericNames.Contains(named.Identifier.Text) && arguments.Length == 0 => new VelaType(named.Identifier.Text),
            _ when _records.ContainsKey(named.Identifier.Text) || _objects.ContainsKey(named.Identifier.Text) || _enums.ContainsKey(named.Identifier.Text) => new VelaType(named.Identifier.Text, arguments),
            _ => ReportUnknownType(named)
        };

        ValidateTypeArgumentCount(type, named.Span, arguments.Length);
        if (named.QuestionToken is not null)
        {
            type = new VelaType("Option", [type]);
        }

        return type;
    }

    private VelaType ReportVoidValueType(NamedTypeSyntax named)
    {
        Report("VEL3020", named.Span, "Void is return-only and cannot be used as a value type.", "Use Void only after '->' on a function or method declaration.");
        return VelaType.Unknown;
    }

    private VelaType ReportUnknownType(NamedTypeSyntax named)
    {
        Report("VEL3001", named.Span, $"Unknown type '{named.Identifier.Text}'.", "Use a built-in type, a generic parameter, or a declared record.");
        return VelaType.Unknown;
    }

    private void ValidateTypeArgumentCount(VelaType type, TextSpan span, int actualCount)
    {
        int expected = type.Name switch
        {
            "List" or "Array" or "Option" or "Future" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList" => 1,
            "Result" or "HashMap" or "SortedMap" => 2,
            "BitSet" => 0,
            _ when _records.TryGetValue(type.Name, out var record) => record.GenericNames.Length,
            _ when _objects.TryGetValue(type.Name, out var objectDeclaration) => objectDeclaration.GenericNames.Length,
            _ when _enums.ContainsKey(type.Name) => 0,
            _ => 0
        };

        if (type.Name != "<unknown>" && actualCount != expected)
        {
            Report("VEL3006", span, $"Type '{type.Name}' expects {expected} type argument(s), but received {actualCount}.");
        }
    }

    private void ValidateGenericConstraints(IReadOnlyList<GenericParameterSyntax> parameters)
    {
        foreach (var parameter in parameters.Where(static parameter => parameter.Constraint is not null))
        {
            Report("VEL3010", parameter.Span, $"Generic constraint on '{parameter.Identifier.Text}' is not available in this compiler version.", "Remove the constraint or enforce it through a runtime contract.");
        }
    }

    private void ValidateParameterDefault(ParameterSyntax parameter, VelaType parameterType, Scope scope)
    {
        if (parameter.DefaultValue is null)
        {
            return;
        }

        var value = EmitExpression(parameter.DefaultValue, scope);
        EnsureAssignable(parameterType, value.Type, parameter.DefaultValue.Span);
    }

    private void ValidateAttributes(IReadOnlyList<AttributeSyntax> attributes, AttributeTarget target)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (!names.Add(attribute.Name.Text))
            {
                Report("VEL3026", attribute.Name.Span, $"Attribute '@{attribute.Name.Text}' cannot be applied more than once to the same declaration.", "Remove the duplicate attribute.");
                continue;
            }

            switch (attribute.Name.Text)
            {
                case "deprecated":
                    RequireAttributeStringArgument(attribute);
                    break;
                case "experimental":
                    RequireAttributeArgumentCount(attribute, 0);
                    break;
                case "since":
                    RequireAttributeStringArgument(attribute);
                    if (attribute.Arguments.Count == 1
                        && attribute.Arguments[0] is LiteralExpressionSyntax { LiteralToken.Value: string version }
                        && !Version.TryParse(version, out _))
                    {
                        Report("VEL3026", attribute.Arguments[0].Span, $"Attribute '@since' requires a valid version, but '{version}' is invalid.", "Use a version such as '0.2.0'.");
                    }

                    break;
                case "doc":
                    RequireAttributeArgumentCount(attribute, 1);
                    if (attribute.Arguments.Count == 1 && attribute.Arguments[0] is not NameExpressionSyntax { Identifier.Text: "hidden" })
                    {
                        Report("VEL3026", attribute.Arguments[0].Span, "Attribute '@doc' currently supports only the 'hidden' argument.", "Use '@doc(hidden)'.");
                    }

                    break;
                default:
                    Report("VEL3026", attribute.Name.Span, $"Unknown attribute '@{attribute.Name.Text}'.", "Use @deprecated, @experimental, @since, or @doc(hidden).");
                    break;
            }

            if (attribute.Name.Text == "doc" && target == AttributeTarget.Local)
            {
                Report("VEL3026", attribute.Span, "Attribute '@doc' is not valid on a local declaration.");
            }
        }
    }

    private void ReportAttributeUse(IReadOnlyList<AttributeSyntax> attributes, TextSpan useSpan, string declaration)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute.Name.Text)
            {
                case "deprecated":
                    var detail = attribute.Arguments.Count == 1
                        && attribute.Arguments[0] is LiteralExpressionSyntax { LiteralToken.Value: string message }
                            ? " " + message
                            : string.Empty;
                    _diagnostics.ReportWarning("VELW002", useSpan, $"Use of deprecated {declaration}.{detail}", "Migrate to the supported replacement described by the declaration.");
                    break;
                case "experimental":
                    _diagnostics.ReportWarning("VELW003", useSpan, $"Use of experimental {declaration}; its API may change.", "Pin the Vela version and review release notes before production use.");
                    break;
            }
        }
    }

    private void RequireAttributeStringArgument(AttributeSyntax attribute)
    {
        RequireAttributeArgumentCount(attribute, 1);
        if (attribute.Arguments.Count == 1 && attribute.Arguments[0] is not LiteralExpressionSyntax { LiteralToken.Kind: TokenKind.StringLiteral })
        {
            Report("VEL3026", attribute.Arguments[0].Span, $"Attribute '@{attribute.Name.Text}' requires one Text literal.", $"Use '@{attribute.Name.Text}(\"message\")'.");
        }
    }

    private void RequireAttributeArgumentCount(AttributeSyntax attribute, int expected)
    {
        if (attribute.Arguments.Count != expected)
        {
            Report("VEL3026", attribute.Span, $"Attribute '@{attribute.Name.Text}' expects {expected} argument(s), but received {attribute.Arguments.Count}.");
        }
    }

    private static string ConstructorCaptureName(string name) => "__velaCtor_" + EscapeIdentifier(name);

    private void InferGenericArguments(VelaType expected, VelaType actual, IReadOnlyCollection<string> genericNames, IDictionary<string, VelaType> substitutions)
    {
        if (genericNames.Contains(expected.Name) && expected.TypeArguments.Count == 0)
        {
            if (substitutions.TryGetValue(expected.Name, out var existing))
            {
                EnsureAssignable(existing, actual, default);
            }
            else
            {
                substitutions[expected.Name] = actual;
            }

            return;
        }

        if (expected.Name != actual.Name || expected.TypeArguments.Count != actual.TypeArguments.Count)
        {
            return;
        }

        for (var index = 0; index < expected.TypeArguments.Count; index++)
        {
            InferGenericArguments(expected.TypeArguments[index], actual.TypeArguments[index], genericNames, substitutions);
        }
    }

    private static Dictionary<string, VelaType> CreateGenericSubstitutions(string[] genericNames, VelaType[] explicitTypes)
    {
        var substitutions = new Dictionary<string, VelaType>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(genericNames.Length, explicitTypes.Length); index++)
        {
            substitutions[genericNames[index]] = explicitTypes[index];
        }

        return substitutions;
    }

    private static bool TryGetNumericType(string name, out VelaType type)
    {
        type = name switch
        {
            "Int" => VelaType.Int,
            "UInt" => VelaType.UInt,
            "Long" => VelaType.Long,
            "Float" => VelaType.Float,
            "Double" => VelaType.Double,
            "Decimal" => VelaType.Decimal,
            _ => VelaType.Unknown
        };
        return !type.IsUnknown;
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

    private VelaType GetNumericResultType(VelaType left, VelaType right, TextSpan span)
    {
        if (!left.IsNumeric || !right.IsNumeric)
        {
            return VelaType.Unknown;
        }

        if (left.IsSameAs(right))
        {
            return left;
        }

        if (left.IsSameAs(VelaType.Decimal) || right.IsSameAs(VelaType.Decimal))
        {
            if (left.IsSameAs(VelaType.Float) || left.IsSameAs(VelaType.Double) || right.IsSameAs(VelaType.Float) || right.IsSameAs(VelaType.Double))
            {
                Report("VEL3002", span, "Decimal values cannot be combined with Float or Double values.", "Convert both operands explicitly to Decimal or to the same binary floating-point type.");
                return VelaType.Unknown;
            }

            return VelaType.Decimal;
        }

        if (left.IsSameAs(VelaType.Double) || right.IsSameAs(VelaType.Double))
        {
            return VelaType.Double;
        }

        if (left.IsSameAs(VelaType.Float) || right.IsSameAs(VelaType.Float))
        {
            return VelaType.Float;
        }

        if (left.IsSameAs(VelaType.Long) || right.IsSameAs(VelaType.Long))
        {
            if (left.IsSameAs(VelaType.UInt) || right.IsSameAs(VelaType.UInt))
            {
                Report("VEL3002", span, "UInt values cannot be combined implicitly with signed integer values.", "Convert one operand explicitly before performing arithmetic.");
                return VelaType.Unknown;
            }

            return VelaType.Long;
        }

        if (left.IsSameAs(VelaType.UInt) || right.IsSameAs(VelaType.UInt))
        {
            Report("VEL3002", span, "UInt values cannot be combined implicitly with Int values.", "Convert one operand explicitly before performing arithmetic.");
            return VelaType.Unknown;
        }

        return VelaType.Int;
    }

    private string CoerceNumeric(VelaType targetType, ExpressionResult expression, SyntaxNode source)
    {
        if (targetType.IsSameAs(expression.Type) || targetType.IsUnknown || expression.Type.IsUnknown)
        {
            return expression.Code;
        }

        var conversion = targetType.Name switch
        {
            "Int" => "ToInt",
            "UInt" => "ToUInt",
            "Long" => "ToLong",
            "Float" => "ToFloat",
            "Double" => "ToDouble",
            "Decimal" => "ToDecimal",
            _ => string.Empty
        };
        return string.IsNullOrEmpty(conversion)
            ? expression.Code
            : $"VelaNumeric.{conversion}({expression.Code}, {SourceLocationCode(source)})";
    }

    private string CoerceCode(VelaType expected, ExpressionResult actual, SyntaxNode source)
    {
        if (expected.IsOptional && actual.Type.IsSameAs(VelaType.Nil))
        {
            return "default";
        }

        if (expected.IsNumeric && actual.Type.IsNumeric && !expected.IsSameAs(actual.Type))
        {
            return CoerceNumeric(expected, actual, source);
        }

        return actual.Code;
    }

    private static bool CanWidenNumeric(VelaType expected, VelaType actual) =>
        actual.IsSameAs(VelaType.Int) && expected.Name is "Long" or "Float" or "Double" or "Decimal"
        || actual.IsSameAs(VelaType.UInt) && expected.Name is "Long" or "Float" or "Double" or "Decimal"
        || actual.IsSameAs(VelaType.Long) && expected.Name is "Float" or "Double" or "Decimal"
        || actual.IsSameAs(VelaType.Float) && expected.IsSameAs(VelaType.Double);

    private string SourceLocationCode(SyntaxNode syntax)
    {
        var location = _getLocation(syntax.Span);
        return QuoteString($"{location.FilePath}:{location.Line}:{location.Column}");
    }

    private void EnsureAssignable(VelaType expected, VelaType actual, TextSpan span)
    {
        if (actual.IsSameAs(VelaType.TcpConnection) && expected.IsSameAs(VelaType.Any))
        {
            Report("VEL3010", span, "TcpConnection cannot be boxed as Any.", "Keep the connection in its explicit TcpConnection binding and close it with tcp.close.");
            return;
        }

        if (expected.IsUnknown || actual.IsUnknown || expected.IsSameAs(actual) || expected.IsSameAs(VelaType.Any) || (actual.IsSameAs(VelaType.Nil) && expected.IsOptional) || CanWidenNumeric(expected, actual))
        {
            return;
        }

        Report("VEL3002", span, $"Type mismatch: expected {expected}, found {actual}.", "Change the value or update the declared type.");
    }

    private void EnsureComparable(VelaType left, VelaType right, TextSpan span)
    {
        if (left.IsUnknown || right.IsUnknown || left.IsSameAs(right) || (left.IsNumeric && right.IsNumeric) || left.IsSameAs(VelaType.Nil) || right.IsSameAs(VelaType.Nil))
        {
            return;
        }

        Report("VEL3002", span, $"Values of type {left} and {right} cannot be compared.");
    }

    private void EnsureHashable(VelaType type, TextSpan span)
    {
        if (type.IsUnknown || type.Name is "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal" or "Bool" or "Text" or "Option" or "Result" || _records.ContainsKey(type.Name) || _enums.ContainsKey(type.Name) || _objects.TryGetValue(type.Name, out var objectSymbol) && objectSymbol.Syntax.Kind == ObjectDeclarationKind.Struct)
        {
            return;
        }

        Report("VEL3006", span, $"Type '{type}' cannot be used as a hash key.", "Use a primitive value, an immutable record, Option, or Result as the key.");
    }

    private void EnsureOrdered(VelaType type, TextSpan span)
    {
        if (type.IsUnknown || type.Name is "Int" or "UInt" or "Long" or "Float" or "Double" or "Decimal" or "Bool" or "Text")
        {
            return;
        }

        Report("VEL3006", span, $"Type '{type}' does not have a defined ordering.", "Use a numeric type, Bool, or Text for ordered collections.");
    }

    private VelaType GetIterationElementType(VelaType collectionType, TextSpan span)
    {
        if (collectionType.Name is "List" or "Array" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "SortedSet" or "Deque" or "LinkedList" && collectionType.TypeArguments.Count == 1)
        {
            return collectionType.TypeArguments[0];
        }

        Report("VEL3006", span, $"Type '{collectionType}' cannot be iterated.", "Use Vector, HashSet, SortedSet, Queue, Stack, RingBuffer, Deque, or LinkedList in a for loop.");
        return VelaType.Unknown;
    }

    private static bool HasCountProperty(VelaType type) => type.Name is "List" or "HashMap" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "SortedMap" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList";

    private static bool HasCapacityProperty(VelaType type) => type.Name is "List" or "Queue" or "RingBuffer" or "BitSet" or "Deque" or "PriorityQueue";

    private static bool IsCollectionConstructor(string name) => name is "List" or "Vector" or "Array" or "HashMap" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "BitSet" or "SortedMap" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList";

    private void AddVariable(Scope scope, string name, VariableSymbol symbol, TextSpan span)
    {
        if (!scope.TryAdd(name, symbol))
        {
            Report("VEL3000", span, $"Duplicate local binding '{name}'.", "Use a distinct name in this scope.");
        }
    }

    private void WriteLineDirective(SyntaxNode syntax)
    {
        var location = _getLocation(syntax.Span);
        var path = location.FilePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        _writer.WriteLine($"#line {location.Line.ToString(CultureInfo.InvariantCulture)} \"{path}\"");
    }

    private static string CSharpType(VelaType type) => type.Name switch
    {
        "Int" => "int",
        "UInt" => "uint",
        "Long" => "long",
        "Float" => "float",
        "Double" => "double",
        "Decimal" => "decimal",
        "Bool" => "bool",
        "Text" => "string",
        "Any" => "object",
        "TcpConnection" => "TcpConnection",
        "GuiForm" => "GuiForm",
        "GuiControl" => "GuiControl",
        "HttpServer" => "HttpServer",
        "GraphqlSchema" => "VelaGraphqlSchema",
        "GrpcServer" => "GrpcServer",
        "SqliteDatabase" => "VelaSqliteDatabase",
        "PostgresDatabase" => "VelaPostgresDatabase",
        "Cancellation" => "VelaCancellation",
        "ProcessResult" => "VelaProcessResult",
        "Fn" => VelaCallbackEmitter.CSharpDelegateType(type),
        "Unit" => "void",
        "Option" when type.TypeArguments.Count == 1 => $"Option<{CSharpType(type.TypeArguments[0])}>",
        "Result" when type.TypeArguments.Count == 2 => $"Result<{CSharpType(type.TypeArguments[0])}, {CSharpType(type.TypeArguments[1])}>",
        "Future" when type.TypeArguments.Count == 1 => CSharpTaskType(type.TypeArguments[0]),
        "List" when type.TypeArguments.Count == 1 => $"VelaVector<{CSharpType(type.TypeArguments[0])}>",
        "Array" when type.TypeArguments.Count == 1 => $"VelaArray<{CSharpType(type.TypeArguments[0])}>",
        "HashMap" when type.TypeArguments.Count == 2 => $"VelaHashMap<{CSharpType(type.TypeArguments[0])}, {CSharpType(type.TypeArguments[1])}>",
        "HashSet" when type.TypeArguments.Count == 1 => $"VelaHashSet<{CSharpType(type.TypeArguments[0])}>",
        "Queue" when type.TypeArguments.Count == 1 => $"VelaQueue<{CSharpType(type.TypeArguments[0])}>",
        "Stack" when type.TypeArguments.Count == 1 => $"VelaStack<{CSharpType(type.TypeArguments[0])}>",
        "RingBuffer" when type.TypeArguments.Count == 1 => $"RingBuffer<{CSharpType(type.TypeArguments[0])}>",
        "BitSet" => "BitSet",
        "SortedMap" when type.TypeArguments.Count == 2 => $"VelaSortedMap<{CSharpType(type.TypeArguments[0])}, {CSharpType(type.TypeArguments[1])}>",
        "SortedSet" when type.TypeArguments.Count == 1 => $"VelaSortedSet<{CSharpType(type.TypeArguments[0])}>",
        "Deque" when type.TypeArguments.Count == 1 => $"VelaDeque<{CSharpType(type.TypeArguments[0])}>",
        "PriorityQueue" when type.TypeArguments.Count == 1 => $"VelaPriorityQueue<{CSharpType(type.TypeArguments[0])}>",
        "LinkedList" when type.TypeArguments.Count == 1 => $"VelaLinkedList<{CSharpType(type.TypeArguments[0])}>",
        "Nil" => "object?",
        "<unknown>" => "object",
        _ when type.TypeArguments.Count == 0 => EscapeIdentifier(type.Name),
        _ => $"{EscapeIdentifier(type.Name)}<{string.Join(", ", type.TypeArguments.Select(CSharpType))}>"
    };

    private static string CSharpTaskType(VelaType type) => type.IsSameAs(VelaType.Unit)
        ? "Task"
        : $"Task<{CSharpType(type)}>";

    private static string DefaultValue(VelaType type) => type.Name switch
    {
        "Int" => "0",
        "UInt" => "0U",
        "Long" => "0L",
        "Float" => "0f",
        "Double" => "0d",
        "Decimal" => "0m",
        "Bool" => "false",
        "Text" => "string.Empty",
        _ => "default!"
    };

    private static string FormatGenericParameterNames(string[] names) => names.Length == 0
        ? string.Empty
        : $"<{string.Join(", ", names.Select(EscapeIdentifier))}>";

    private static string QuoteString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            _ = character switch
            {
                '\\' => builder.Append("\\\\"),
                '"' => builder.Append("\\\""),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                _ when char.IsControl(character) => builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
                _ => builder.Append(character)
            };
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string EscapeIdentifier(string identifier) => CSharpKeywords.Contains(identifier) ? $"@{identifier}" : identifier;

    private void Report(string code, TextSpan span, string message, string? help = null) => _diagnostics.ReportError(code, span, message, help);

    private sealed record ExpressionResult(VelaType Type, string Code);

    private sealed record BoundCallArguments(
        ExpressionResult[] Values,
        SyntaxNode[] Sources,
        int[] EmissionOrder,
        bool UseNamedEmission,
        CallEvaluationStep[] EvaluationSteps);

    private sealed record CallEvaluationStep(string Name, string Code);

    private sealed record CoreParameter(string Name, VelaType Type, string? DefaultCode = null);

    private sealed record VariableSymbol(VelaType Type, bool Mutable, string? Code = null);

    private sealed record FunctionSymbol(FunctionDeclarationSyntax Syntax)
    {
        public string[] GenericNames { get; } = Syntax.GenericParameters.Select(static parameter => parameter.Identifier.Text).ToArray();
    }

    private sealed record RecordSymbol(RecordDeclarationSyntax Syntax)
    {
        public string[] GenericNames { get; } = Syntax.GenericParameters.Select(static parameter => parameter.Identifier.Text).ToArray();
    }

    private sealed record ObjectSymbol(ObjectDeclarationSyntax Syntax)
    {
        public string[] GenericNames { get; } = Syntax.GenericParameters.Select(static parameter => parameter.Identifier.Text).ToArray();
    }

    private sealed record EnumSymbol(EnumDeclarationSyntax Syntax);

    private enum AttributeTarget
    {
        Function,
        Type,
        Method,
        Field,
        EnumMember,
        Local
    }

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, VariableSymbol> _variables = new(StringComparer.Ordinal);

        public bool TryAdd(string name, VariableSymbol symbol) => _variables.TryAdd(name, symbol);

        public bool TryLookup(string name, out VariableSymbol symbol)
        {
            if (_variables.TryGetValue(name, out symbol!))
            {
                return true;
            }

            return parent is not null && parent.TryLookup(name, out symbol!);
        }
    }
}
