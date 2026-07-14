using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public sealed record RecordDeclarationSyntax(
    SyntaxToken RecordKeyword,
    SyntaxToken Identifier,
    IReadOnlyList<GenericParameterSyntax> GenericParameters,
    SyntaxToken LeftBrace,
    IReadOnlyList<RecordMemberSyntax> Members,
    SyntaxToken RightBrace) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(RecordKeyword.Span.Start, RightBrace.Span.End);
}

public abstract record RecordMemberSyntax : SyntaxNode;

public sealed record RecordFieldSyntax(
    SyntaxToken Identifier,
    SyntaxToken ColonToken,
    TypeSyntax Type) : RecordMemberSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Identifier.Span.Start, Type.Span.End);
}

public sealed record RecordMethodSyntax(FunctionDeclarationSyntax Function) : RecordMemberSyntax
{
    public override TextSpan Span => Function.Span;
}
