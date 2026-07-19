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
        _writer.WriteLine("using System.Text;");
        _writer.WriteLine("using System.Threading.Tasks;");
        _writer.WriteLine("using Vela.Runtime;");
        if (_libraryMode || _importsByAlias.Values.Any(static importItem =>
                importItem.Manifest.AbiVersion >= 2
                || importItem.Manifest.Exports.Any(static exportItem =>
                    exportItem.ReturnType is "Text" or "Decimal"
                    || exportItem.Parameters.Contains("Text")
                    || exportItem.Parameters.Contains("Decimal"))))
        {
            _writer.WriteLine("using Vela.Runtime.Interop;");
        }

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
