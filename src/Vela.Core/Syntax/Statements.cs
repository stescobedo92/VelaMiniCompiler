using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

public abstract record StatementSyntax : SyntaxNode;

public sealed record LetStatementSyntax(
    SyntaxToken LetKeyword,
    SyntaxToken Identifier,
    SyntaxToken? ColonToken,
    TypeSyntax? Type,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LetKeyword.Span.Start, Initializer.Span.End);
}

public sealed record VarStatementSyntax(
    SyntaxToken VarKeyword,
    SyntaxToken Identifier,
    SyntaxToken? ColonToken,
    TypeSyntax? Type,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(VarKeyword.Span.Start, Initializer.Span.End);
}

/// <summary>Binds the elements of a tuple to immutable local names.</summary>
public sealed record TupleDestructuringStatementSyntax(
    SyntaxToken LetKeyword,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<SyntaxToken> Bindings,
    SyntaxToken RightParenthesis,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LetKeyword.Span.Start, Initializer.Span.End);
}

/// <summary>Binds the public fields of a record to immutable local names.</summary>
public sealed record RecordDestructuringStatementSyntax(
    SyntaxToken LetKeyword,
    SyntaxToken RecordType,
    SyntaxToken LeftBrace,
    IReadOnlyList<SyntaxToken> Fields,
    SyntaxToken RightBrace,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(LetKeyword.Span.Start, Initializer.Span.End);
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? PublicKeyword,
    SyntaxToken? FfiKeyword,
    SyntaxToken? AsyncKeyword,
    SyntaxToken FnKeyword,
    SyntaxToken Identifier,
    IReadOnlyList<GenericParameterSyntax> GenericParameters,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<ParameterSyntax> Parameters,
    SyntaxToken RightParenthesis,
    SyntaxToken? ArrowToken,
    TypeSyntax? ReturnType,
    SyntaxToken LeftBrace,
    BlockSyntax Body,
    DocumentationCommentSyntax? Documentation = null,
    IReadOnlyList<AttributeSyntax>? Attributes = null) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((PublicKeyword ?? FfiKeyword ?? AsyncKeyword ?? FnKeyword).Span.Start, Body.Span.End);
}

public sealed record ReturnStatementSyntax(
    SyntaxToken ReturnKeyword,
    ExpressionSyntax? Expression) : StatementSyntax
{
    public override TextSpan Span => Expression is null
        ? ReturnKeyword.Span
        : TextSpan.FromBounds(ReturnKeyword.Span.Start, Expression.Span.End);
}

public sealed record AssertStatementSyntax(
    SyntaxToken AssertKeyword,
    ExpressionSyntax Condition,
    SyntaxToken? CommaToken,
    ExpressionSyntax? Message) : StatementSyntax
{
    public override TextSpan Span => Message is null
        ? TextSpan.FromBounds(AssertKeyword.Span.Start, Condition.Span.End)
        : TextSpan.FromBounds(AssertKeyword.Span.Start, Message.Span.End);
}

/// <summary>Registers one call to run when its containing block exits.</summary>
public sealed record DeferStatementSyntax(
    SyntaxToken DeferKeyword,
    ExpressionSyntax Invocation) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(DeferKeyword.Span.Start, Invocation.Span.End);
}

/// <summary>Protects a block with typed exception handlers and optional finalization.</summary>
public sealed record TryStatementSyntax(
    SyntaxToken TryKeyword,
    BlockSyntax TryBlock,
    IReadOnlyList<CatchClauseSyntax> Catches,
    FinallyClauseSyntax? FinallyClause) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(TryKeyword.Span.Start, (FinallyClause?.Span ?? (Catches.Count == 0 ? TryBlock.Span : Catches[^1].Span)).End);
}

/// <summary>Handles one named Vela runtime exception type.</summary>
public sealed record CatchClauseSyntax(
    SyntaxToken CatchKeyword,
    TypeSyntax ExceptionType,
    SyntaxToken Identifier,
    BlockSyntax Block) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(CatchKeyword.Span.Start, Block.Span.End);
}

/// <summary>Executes a block after protected execution completes.</summary>
public sealed record FinallyClauseSyntax(
    SyntaxToken FinallyKeyword,
    BlockSyntax Block) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(FinallyKeyword.Span.Start, Block.Span.End);
}

public sealed record IfStatementSyntax(
    SyntaxToken IfKeyword,
    ExpressionSyntax Condition,
    SyntaxToken LeftBrace,
    BlockSyntax ThenBlock,
    ElseClauseSyntax? ElseClause) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(IfKeyword.Span.Start, (ElseClause?.Span ?? ThenBlock.Span).End);
}

public sealed record ForStatementSyntax(
    SyntaxToken ForKeyword,
    SyntaxToken Identifier,
    SyntaxToken InKeyword,
    ExpressionSyntax Collection,
    SyntaxToken LeftBrace,
    BlockSyntax Body) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(ForKeyword.Span.Start, Body.Span.End);
}

/// <summary>Repeats a block while a Boolean condition is true.</summary>
public sealed record WhileStatementSyntax(
    SyntaxToken WhileKeyword,
    ExpressionSyntax Condition,
    SyntaxToken LeftBrace,
    BlockSyntax Body) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(WhileKeyword.Span.Start, Body.Span.End);
}

/// <summary>Exits the nearest enclosing loop.</summary>
public sealed record BreakStatementSyntax(SyntaxToken BreakKeyword) : StatementSyntax
{
    public override TextSpan Span => BreakKeyword.Span;
}

/// <summary>Advances the nearest enclosing loop.</summary>
public sealed record ContinueStatementSyntax(SyntaxToken ContinueKeyword) : StatementSyntax
{
    public override TextSpan Span => ContinueKeyword.Span;
}

/// <summary>Selects one isolated case block from a scalar expression.</summary>
public sealed record SwitchStatementSyntax(
    SyntaxToken SwitchKeyword,
    ExpressionSyntax Expression,
    SyntaxToken LeftBrace,
    IReadOnlyList<SwitchCaseSyntax> Cases,
    SwitchDefaultClauseSyntax? DefaultClause,
    SyntaxToken RightBrace) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(SwitchKeyword.Span.Start, RightBrace.Span.End);
}

/// <summary>Represents one literal case and its isolated block.</summary>
public sealed record SwitchCaseSyntax(
    SyntaxToken CaseKeyword,
    ExpressionSyntax Value,
    BlockSyntax Body) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(CaseKeyword.Span.Start, Body.Span.End);
}

/// <summary>Represents the optional fallback block of a switch statement.</summary>
public sealed record SwitchDefaultClauseSyntax(
    SyntaxToken DefaultKeyword,
    BlockSyntax Body) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(DefaultKeyword.Span.Start, Body.Span.End);
}

/// <summary>Selects a case using an exhaustive enum, Option, Result, or literal pattern.</summary>
public sealed record MatchStatementSyntax(
    SyntaxToken MatchKeyword,
    ExpressionSyntax Expression,
    SyntaxToken LeftBrace,
    IReadOnlyList<MatchCaseSyntax> Cases,
    SwitchDefaultClauseSyntax? DefaultClause,
    SyntaxToken RightBrace) : StatementSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(MatchKeyword.Span.Start, RightBrace.Span.End);
}

/// <summary>Represents one pattern and its isolated case block.</summary>
public sealed record MatchCaseSyntax(
    SyntaxToken CaseKeyword,
    MatchPatternSyntax Pattern,
    BlockSyntax Body) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(CaseKeyword.Span.Start, Body.Span.End);
}

/// <summary>Base syntax for a match pattern.</summary>
public abstract record MatchPatternSyntax : SyntaxNode;

/// <summary>Matches Some, None, Ok, or Err and optionally binds its payload.</summary>
public sealed record VariantMatchPatternSyntax(
    SyntaxToken Variant,
    SyntaxToken? LeftParenthesis,
    SyntaxToken? Binding,
    SyntaxToken? RightParenthesis) : MatchPatternSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(Variant.Span.Start, (RightParenthesis ?? Binding ?? Variant).Span.End);
}

/// <summary>Matches an enum member or scalar literal.</summary>
public sealed record ValueMatchPatternSyntax(ExpressionSyntax Value) : MatchPatternSyntax
{
    public override TextSpan Span => Value.Span;
}

public sealed record ElseClauseSyntax(
    SyntaxToken ElseKeyword,
    SyntaxToken LeftBrace,
    BlockSyntax Block) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(ElseKeyword.Span.Start, Block.Span.End);
}

public sealed record ExpressionStatementSyntax(ExpressionSyntax Expression) : StatementSyntax
{
    public override TextSpan Span => Expression.Span;
}

public sealed record BlockSyntax(
    SyntaxToken LeftBrace,
    IReadOnlyList<StatementSyntax> Statements,
    SyntaxToken RightBrace) : SyntaxNode
{
    public override TextSpan Span => TextSpan.FromBounds(LeftBrace.Span.Start, RightBrace.Span.End);
}
