using Vela.Core.Diagnostics;

namespace Vela.Core.Lexing;

public sealed record LexResult(
    IReadOnlyList<SyntaxToken> Tokens,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<SyntaxTrivia> Trivia);
