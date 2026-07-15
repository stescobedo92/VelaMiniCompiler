using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Declares a strongly typed ordinal value set.</summary>
public sealed record EnumDeclarationSyntax(
    SyntaxToken? PublicKeyword,
    SyntaxToken EnumKeyword,
    SyntaxToken Identifier,
    SyntaxToken LeftBrace,
    IReadOnlyList<EnumMemberSyntax> Members,
    SyntaxToken RightBrace,
    DocumentationCommentSyntax? Documentation = null,
    IReadOnlyList<AttributeSyntax>? Attributes = null) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((PublicKeyword ?? EnumKeyword).Span.Start, RightBrace.Span.End);
}

/// <summary>Declares one named enum value.</summary>
public sealed record EnumMemberSyntax(
    SyntaxToken Identifier,
    SyntaxToken? Separator,
    DocumentationCommentSyntax? Documentation = null,
    IReadOnlyList<AttributeSyntax>? Attributes = null) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(Identifier.Span.Start, (Separator ?? Identifier).Span.End);
}
