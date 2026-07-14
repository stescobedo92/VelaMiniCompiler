using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public abstract record TypeSyntax : SyntaxNode;

public sealed record NamedTypeSyntax(
    SyntaxToken Identifier,
    SyntaxToken? LessToken,
    IReadOnlyList<TypeSyntax> TypeArguments,
    SyntaxToken? GreaterToken,
    SyntaxToken? QuestionToken) : TypeSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Identifier.Span.Start, (QuestionToken ?? GreaterToken ?? Identifier).Span.End);
}

public sealed record GenericParameterSyntax(
    SyntaxToken Identifier,
    SyntaxToken? ColonToken,
    TypeSyntax? Constraint) : SyntaxNode
{
    public override TextSpan Span => Constraint is null
        ? Identifier.Span
        : TextSpan.FromBounds(Identifier.Span.Start, Constraint.Span.End);
}

public sealed record ParameterSyntax(
    SyntaxToken Identifier,
    SyntaxToken ColonToken,
    TypeSyntax Type) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(Identifier.Span.Start, Type.Span.End);
}
