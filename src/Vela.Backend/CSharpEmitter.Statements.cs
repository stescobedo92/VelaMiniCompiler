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
}
