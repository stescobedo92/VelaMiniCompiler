using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public abstract record ExpressionSyntax : SyntaxNode;

public sealed record LiteralExpressionSyntax(SyntaxToken LiteralToken) : ExpressionSyntax
{
    public override TextSpan Span => LiteralToken.Span;
}

public sealed record NameExpressionSyntax(SyntaxToken Identifier) : ExpressionSyntax
{
    public override TextSpan Span => Identifier.Span;
}

public sealed record MemberAccessExpressionSyntax(
    ExpressionSyntax Receiver,
    SyntaxToken DotToken,
    SyntaxToken Member) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Receiver.Span.Start, Member.Span.End);
}

public sealed record IndexExpressionSyntax(
    ExpressionSyntax Receiver,
    SyntaxToken LeftBracket,
    ExpressionSyntax Index,
    SyntaxToken RightBracket) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Receiver.Span.Start, RightBracket.Span.End);
}

public sealed record UnaryExpressionSyntax(
    SyntaxToken OperatorToken,
    ExpressionSyntax Operand) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(OperatorToken.Span.Start, Operand.Span.End);
}

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxToken OperatorToken,
    ExpressionSyntax Right) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Left.Span.Start, Right.Span.End);
}

public sealed record AssignmentExpressionSyntax(
    ExpressionSyntax Target,
    SyntaxToken EqualsToken,
    ExpressionSyntax Value) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Target.Span.Start, Value.Span.End);
}

public sealed record ParenthesizedExpressionSyntax(
    SyntaxToken LeftParenthesis,
    ExpressionSyntax Expression,
    SyntaxToken RightParenthesis) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LeftParenthesis.Span.Start, RightParenthesis.Span.End);
}

public sealed record CallExpressionSyntax(
    ExpressionSyntax Callee,
    SyntaxToken? LessToken,
    IReadOnlyList<TypeSyntax> TypeArguments,
    SyntaxToken? GreaterToken,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<ExpressionSyntax> Arguments,
    SyntaxToken RightParenthesis) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Callee.Span.Start, RightParenthesis.Span.End);
}

public sealed record ListExpressionSyntax(
    SyntaxToken LeftBracket,
    IReadOnlyList<ExpressionSyntax> Elements,
    SyntaxToken RightBracket) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LeftBracket.Span.Start, RightBracket.Span.End);
}
