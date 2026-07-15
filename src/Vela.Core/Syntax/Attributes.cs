using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Represents compile-time metadata attached to a declaration.</summary>
public sealed record AttributeSyntax(
    SyntaxToken AtToken,
    SyntaxToken Name,
    SyntaxToken? LeftParenthesis,
    IReadOnlyList<ExpressionSyntax> Arguments,
    SyntaxToken? RightParenthesis) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(AtToken.Span.Start, (RightParenthesis ?? Name).Span.End);
}
