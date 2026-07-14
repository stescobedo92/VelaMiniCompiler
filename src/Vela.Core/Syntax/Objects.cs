using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Describes the source-level kind of an object declaration.</summary>
public enum ObjectDeclarationKind
{
    Class,
    Struct,
    Interface
}

/// <summary>Declares a Vela class, struct, or interface.</summary>
public sealed record ObjectDeclarationSyntax(
    SyntaxToken? PublicKeyword,
    SyntaxToken Keyword,
    ObjectDeclarationKind Kind,
    SyntaxToken Identifier,
    IReadOnlyList<GenericParameterSyntax> GenericParameters,
    SyntaxToken? ImplementsKeyword,
    IReadOnlyList<TypeSyntax> ImplementedInterfaces,
    SyntaxToken LeftBrace,
    IReadOnlyList<ObjectMemberSyntax> Members,
    SyntaxToken RightBrace,
    DocumentationCommentSyntax? Documentation = null) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((PublicKeyword ?? Keyword).Span.Start, RightBrace.Span.End);
}

/// <summary>Base type for a member of a Vela class, struct, or interface.</summary>
public abstract record ObjectMemberSyntax : SyntaxNode;

/// <summary>Declares an instance field. Fields are mutable only when prefixed with <c>var</c>.</summary>
public sealed record ObjectFieldSyntax(
    SyntaxToken? VarKeyword,
    SyntaxToken Identifier,
    SyntaxToken ColonToken,
    TypeSyntax Type,
    DocumentationCommentSyntax? Documentation = null) : ObjectMemberSyntax
{
    public bool IsMutable => VarKeyword is not null;

    public override TextSpan Span => TextSpan.FromBounds((VarKeyword ?? Identifier).Span.Start, Type.Span.End);
}

/// <summary>Declares an implemented object method.</summary>
public sealed record ObjectMethodSyntax(FunctionDeclarationSyntax Function) : ObjectMemberSyntax
{
    public override TextSpan Span => Function.Span;
}

/// <summary>Declares an interface method without an implementation body.</summary>
public sealed record InterfaceMethodSyntax(
    SyntaxToken? AsyncKeyword,
    SyntaxToken FnKeyword,
    SyntaxToken Identifier,
    IReadOnlyList<GenericParameterSyntax> GenericParameters,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<ParameterSyntax> Parameters,
    SyntaxToken RightParenthesis,
    SyntaxToken? ArrowToken,
    TypeSyntax? ReturnType,
    DocumentationCommentSyntax? Documentation = null) : ObjectMemberSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((AsyncKeyword ?? FnKeyword).Span.Start, ReturnType?.Span.End ?? RightParenthesis.Span.End);
}
