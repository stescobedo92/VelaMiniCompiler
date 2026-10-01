using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Syntax;

namespace Vela.Core.Analysis;

/// <summary>Reports advisory findings about valid Vela source.</summary>
/// <remarks>
/// The linter only walks the parse tree, so it runs after a successful
/// compilation and never changes whether a program builds. Findings are
/// warnings: informative by default, failures only under an explicit
/// deny-warnings mode. Run it on error-free trees; trees with parse errors
/// can produce follow-on findings.
/// </remarks>
public static class VelaLinter
{
    /// <summary>Analyzes an error-free <paramref name="root"/> and returns its warnings in walk order.</summary>
    public static Diagnostic[] Analyze(CompilationUnitSyntax root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var walker = new Walker();
        walker.VisitRoot(root);
        return walker.Findings.ToArray();
    }

    private sealed class Walker
    {
        public readonly List<Diagnostic> Findings = [];

        public void VisitRoot(CompilationUnitSyntax root)
        {
            foreach (var member in root.Members)
            {
                VisitStatement(member);
            }
        }

        private void VisitStatement(StatementSyntax statement)
        {
            switch (statement)
            {
                case LetStatementSyntax let:
                    VisitExpression(let.Initializer);
                    break;
                case VarStatementSyntax variable:
                    VisitExpression(variable.Initializer);
                    break;
                case TupleDestructuringStatementSyntax tuple:
                    VisitExpression(tuple.Initializer);
                    break;
                case RecordDestructuringStatementSyntax record:
                    VisitExpression(record.Initializer);
                    break;
                case FunctionDeclarationSyntax function:
                    VisitBlock(function.Body);
                    break;
                case ReturnStatementSyntax @return:
                    if (@return.Expression is not null)
                    {
                        VisitExpression(@return.Expression);
                    }

                    break;
                case AssertStatementSyntax assert:
                    VisitExpression(assert.Condition);
                    if (assert.Message is not null)
                    {
                        VisitExpression(assert.Message);
                    }

                    break;
                case DeferStatementSyntax defer:
                    VisitExpression(defer.Invocation);
                    break;
                case TryStatementSyntax @try:
                    VisitBlock(@try.TryBlock);
                    foreach (var clause in @try.Catches)
                    {
                        VisitBlock(clause.Block);
                    }

                    if (@try.FinallyClause is not null)
                    {
                        VisitBlock(@try.FinallyClause.Block);
                    }

                    break;
                case IfStatementSyntax @if:
                    VisitExpression(@if.Condition);
                    VisitBlock(@if.ThenBlock);
                    if (@if.ElseClause is not null)
                    {
                        VisitBlock(@if.ElseClause.Block);
                    }

                    break;
                case ForStatementSyntax @for:
                    VisitExpression(@for.Collection);
                    VisitBlock(@for.Body);
                    break;
                case WhileStatementSyntax @while:
                    VisitExpression(@while.Condition);
                    VisitBlock(@while.Body);
                    break;
                case SwitchStatementSyntax @switch:
                    VisitExpression(@switch.Expression);
                    foreach (var @case in @switch.Cases)
                    {
                        VisitExpression(@case.Value);
                        VisitBlock(@case.Body);
                    }

                    if (@switch.DefaultClause is not null)
                    {
                        VisitBlock(@switch.DefaultClause.Body);
                    }

                    break;
                case MatchStatementSyntax match:
                    VisitExpression(match.Expression);
                    foreach (var @case in match.Cases)
                    {
                        if (@case.Pattern is ValueMatchPatternSyntax value)
                        {
                            VisitExpression(value.Value);
                        }

                        VisitBlock(@case.Body);
                    }

                    if (match.DefaultClause is not null)
                    {
                        VisitBlock(match.DefaultClause.Body);
                    }

                    break;
                case ExpressionStatementSyntax expression:
                    VisitExpression(expression.Expression);
                    break;
                case ObjectDeclarationSyntax @object:
                    foreach (var member in @object.Members)
                    {
                        if (member is ObjectMethodSyntax method)
                        {
                            VisitBlock(method.Function.Body);
                        }
                        else if (member is ObjectFieldSyntax field && field.Initializer is not null)
                        {
                            VisitExpression(field.Initializer);
                        }
                    }

                    break;
                case RecordDeclarationSyntax record:
                    foreach (var member in record.Members)
                    {
                        if (member is RecordMethodSyntax method)
                        {
                            VisitBlock(method.Function.Body);
                        }
                    }

                    break;
                case BreakStatementSyntax:
                case ContinueStatementSyntax:
                case IncludeDirectiveSyntax:
                case EnumDeclarationSyntax:
                    break;
                default:
                    break;
            }
        }

        private void VisitBlock(BlockSyntax block)
        {
            if (block.Statements.Count == 0
                && block.LeftBrace.Text.Length != 0
                && block.RightBrace.Text.Length != 0)
            {
                Findings.Add(new Diagnostic(
                    "VET1002",
                    DiagnosticSeverity.Warning,
                    block.Span,
                    "Empty block does nothing.",
                    "Remove the block or add the missing logic."));
            }

            var terminated = false;
            foreach (var statement in block.Statements)
            {
                if (terminated)
                {
                    Findings.Add(new Diagnostic(
                        "VET1003",
                        DiagnosticSeverity.Warning,
                        statement.Span,
                        "Unreachable statement.",
                        "Statements after 'return', 'break', or 'continue' never run; remove them or move them before it."));
                }

                if (statement is ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax)
                {
                    terminated = true;
                }

                VisitStatement(statement);
            }
        }

        private void VisitExpression(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case LiteralExpressionSyntax:
                case NameExpressionSyntax:
                    break;
                case BinaryExpressionSyntax binary:
                    if (binary.OperatorToken.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals
                        && (IsBooleanLiteral(binary.Left) || IsBooleanLiteral(binary.Right)))
                    {
                        Findings.Add(new Diagnostic(
                            "VET1001",
                            DiagnosticSeverity.Warning,
                            binary.Span,
                            "Comparison with a boolean literal is redundant.",
                            "Write the condition directly instead of comparing it with 'true' or 'false'."));
                    }

                    VisitExpression(binary.Left);
                    VisitExpression(binary.Right);
                    break;
                case UnaryExpressionSyntax unary:
                    VisitExpression(unary.Operand);
                    break;
                case AwaitExpressionSyntax @await:
                    VisitExpression(@await.Expression);
                    break;
                case AssignmentExpressionSyntax assignment:
                    VisitExpression(assignment.Target);
                    VisitExpression(assignment.Value);
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    VisitExpression(parenthesized.Expression);
                    break;
                case TupleExpressionSyntax tuple:
                    foreach (var element in tuple.Elements)
                    {
                        VisitExpression(element);
                    }

                    break;
                case CallExpressionSyntax call:
                    VisitExpression(call.Callee);
                    foreach (var argument in call.Arguments)
                    {
                        VisitExpression(argument.Expression);
                    }

                    break;
                case ListExpressionSyntax list:
                    foreach (var element in list.Elements)
                    {
                        VisitExpression(element);
                    }

                    break;
                case IndexExpressionSyntax index:
                    VisitExpression(index.Receiver);
                    VisitExpression(index.Index);
                    break;
                case MemberAccessExpressionSyntax member:
                    VisitExpression(member.Receiver);
                    break;
                case LambdaExpressionSyntax lambda:
                    VisitBlock(lambda.Body);
                    break;
                default:
                    break;
            }
        }

        private static bool IsBooleanLiteral(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal
            && literal.LiteralToken.Kind is TokenKind.TrueKeyword or TokenKind.FalseKeyword;
    }
}
