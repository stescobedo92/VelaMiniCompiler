using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Imports a package declared by the current Vela manifest.</summary>
public sealed record IncludeDirectiveSyntax(
    SyntaxToken IncludeKeyword,
    IReadOnlyList<SyntaxToken> PackageSegments,
    IReadOnlyList<SyntaxToken> DotTokens,
    SyntaxToken? AsKeyword,
    SyntaxToken? Alias) : StatementSyntax
{
    /// <summary>Gets the dotted package identity as it appeared in source.</summary>
    public string PackageName => string.Join(".", PackageSegments.Select(static segment => segment.Text));

    public override TextSpan Span => TextSpan.FromBounds(IncludeKeyword.Span.Start, (Alias ?? (PackageSegments.Count == 0 ? IncludeKeyword : PackageSegments[^1])).Span.End);
}
