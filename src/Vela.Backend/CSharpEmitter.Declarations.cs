using System.Globalization;
using System.Text;
using Vela.Backend.Emission;
using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Backend;

internal sealed partial class CSharpEmitter
{
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
}
