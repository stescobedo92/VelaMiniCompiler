using Vela.Core.Source;

namespace Vela.Core.Diagnostics;

/// <summary>Collects independent diagnostics so the compiler can report more than one error.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void ReportError(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message, help));

    public void ReportWarning(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, DiagnosticSeverity.Warning, span, message, help));

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _items.AddRange(diagnostics);
    }
}
