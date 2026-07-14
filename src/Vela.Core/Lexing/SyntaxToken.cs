using Vela.Core.Source;

namespace Vela.Core.Lexing;

/// <summary>A lexical token and the exact source text that produced it.</summary>
public sealed record SyntaxToken(TokenKind Kind, TextSpan Span, string Text, object? Value = null)
{
    public static SyntaxToken Missing(TokenKind expectedKind, int position) =>
        new(expectedKind, new TextSpan(position, 0), string.Empty);
}
