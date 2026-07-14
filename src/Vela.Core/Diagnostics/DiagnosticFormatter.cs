using Vela.Core.Source;

namespace Vela.Core.Diagnostics;

/// <summary>Renders structured diagnostics in a stable command-line format.</summary>
public static class DiagnosticFormatter
{
    public static string Format(SourceText source, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostic);

        var location = source.GetLocation(diagnostic.Span);
        var lineIndex = location.Line - 1;
        var line = source.GetLineText(lineIndex);
        var markerLength = Math.Max(1, Math.Min(diagnostic.Span.Length, Math.Max(1, line.Length - location.Column + 1)));
        var marker = new string(' ', Math.Max(0, location.Column - 1)) + new string('^', markerLength);
        var prefix = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var result = $"{prefix} {diagnostic.Code}: {diagnostic.Message} at {location.FilePath}:{location.Line}:{location.Column}{Environment.NewLine}" +
                     $"  |{Environment.NewLine}" +
                     $"{location.Line,2} | {line}{Environment.NewLine}" +
                     $"  | {marker}";

        return diagnostic.Help is null
            ? result
            : result + Environment.NewLine + $"  = help: {diagnostic.Help}";
    }
}
