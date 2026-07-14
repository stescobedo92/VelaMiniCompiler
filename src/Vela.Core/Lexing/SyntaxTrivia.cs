using Vela.Core.Source;

namespace Vela.Core.Lexing;

/// <summary>Classifies source text that is preserved for tooling but does not participate in grammar.</summary>
public enum SyntaxTriviaKind
{
    LineComment,
    BlockComment,
    DocumentationLineComment,
    DocumentationBlockComment
}

/// <summary>Represents a comment with its original source span and spelling.</summary>
public sealed record SyntaxTrivia(SyntaxTriviaKind Kind, TextSpan Span, string Text)
{
    /// <summary>Gets whether this trivia can document the declaration that follows it.</summary>
    public bool IsDocumentation => Kind is SyntaxTriviaKind.DocumentationLineComment or SyntaxTriviaKind.DocumentationBlockComment;
}
