using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public sealed record CompilationUnitSyntax(
    IReadOnlyList<StatementSyntax> Members,
    SyntaxToken EndOfFileToken) : SyntaxNode
{
    public override TextSpan Span => Members.Count == 0
        ? EndOfFileToken.Span
        : TextSpan.FromBounds(Members[0].Span.Start, EndOfFileToken.Span.End);
}
