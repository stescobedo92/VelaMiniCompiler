using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public abstract record StatementSyntax : SyntaxNode;

public sealed record LetStatementSyntax(
    SyntaxToken LetKeyword,
    SyntaxToken Identifier,
    SyntaxToken? ColonToken,
    TypeSyntax? Type,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LetKeyword.Span.Start, Initializer.Span.End);
}

public sealed record VarStatementSyntax(
    SyntaxToken VarKeyword,
    SyntaxToken Identifier,
    SyntaxToken? ColonToken,
    TypeSyntax? Type,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(VarKeyword.Span.Start, Initializer.Span.End);
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? PublicKeyword,
    SyntaxToken? FfiKeyword,
    SyntaxToken FnKeyword,
    SyntaxToken Identifier,
    IReadOnlyList<GenericParameterSyntax> GenericParameters,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<ParameterSyntax> Parameters,
    SyntaxToken RightParenthesis,
    SyntaxToken? ArrowToken,
    TypeSyntax? ReturnType,
    SyntaxToken LeftBrace,
    BlockSyntax Body) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((PublicKeyword ?? FfiKeyword ?? FnKeyword).Span.Start, Body.Span.End);
}

public sealed record ReturnStatementSyntax(
    SyntaxToken ReturnKeyword,
    ExpressionSyntax? Expression) : StatementSyntax
{
    public override TextSpan Span => Expression is null
        ? ReturnKeyword.Span
        : TextSpan.FromBounds(ReturnKeyword.Span.Start, Expression.Span.End);
}

public sealed record AssertStatementSyntax(
    SyntaxToken AssertKeyword,
    ExpressionSyntax Condition,
    SyntaxToken? CommaToken,
    ExpressionSyntax? Message) : StatementSyntax
{
    public override TextSpan Span => Message is null
        ? TextSpan.FromBounds(AssertKeyword.Span.Start, Condition.Span.End)
        : TextSpan.FromBounds(AssertKeyword.Span.Start, Message.Span.End);
}

public sealed record IfStatementSyntax(
    SyntaxToken IfKeyword,
    ExpressionSyntax Condition,
    SyntaxToken LeftBrace,
    BlockSyntax ThenBlock,
    ElseClauseSyntax? ElseClause) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(IfKeyword.Span.Start, (ElseClause?.Span ?? ThenBlock.Span).End);
}

public sealed record ForStatementSyntax(
    SyntaxToken ForKeyword,
    SyntaxToken Identifier,
    SyntaxToken InKeyword,
    ExpressionSyntax Collection,
    SyntaxToken LeftBrace,
    BlockSyntax Body) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(ForKeyword.Span.Start, Body.Span.End);
}

public sealed record ElseClauseSyntax(
    SyntaxToken ElseKeyword,
    SyntaxToken LeftBrace,
    BlockSyntax Block) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(ElseKeyword.Span.Start, Block.Span.End);
}

public sealed record ExpressionStatementSyntax(ExpressionSyntax Expression) : StatementSyntax
{
    public override TextSpan Span => Expression.Span;
}

public sealed record BlockSyntax(
    SyntaxToken LeftBrace,
    IReadOnlyList<StatementSyntax> Statements,
    SyntaxToken RightBrace) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(LeftBrace.Span.Start, RightBrace.Span.End);
}
