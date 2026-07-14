using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Base type for all parsed Vela syntax nodes.</summary>
public abstract record SyntaxNode
{
    public abstract TextSpan Span { get; }
}
