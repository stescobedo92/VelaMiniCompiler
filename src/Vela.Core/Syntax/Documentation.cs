using System.Text;
using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Syntax;

/// <summary>Preserves documentation comments attached to one declaration.</summary>
public sealed record DocumentationCommentSyntax(IReadOnlyList<SyntaxTrivia> Comments) : SyntaxNode
{
    /// <summary>Gets the source range covering the complete documentation group.</summary>
    public override TextSpan Span => Comments.Count == 0
        ? default
        : TextSpan.FromBounds(Comments[0].Span.Start, Comments[^1].Span.End);

    /// <summary>Gets normalized documentation text without comment delimiters.</summary>
    public string Text => string.Join("\n", Comments.Select(Clean));

    private static string Clean(SyntaxTrivia trivia)
    {
        var text = trivia.Text;
        if (trivia.Kind == SyntaxTriviaKind.DocumentationLineComment)
        {
            return text.Length <= 3 ? string.Empty : text[3..].TrimStart();
        }

        if (text.StartsWith("/**", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        if (text.EndsWith("*/", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        var builder = new StringBuilder();
        foreach (var line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var normalized = line.Trim();
            if (normalized.StartsWith('*'))
            {
                normalized = normalized[1..].TrimStart();
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(normalized);
        }

        return builder.ToString().Trim();
    }
}
