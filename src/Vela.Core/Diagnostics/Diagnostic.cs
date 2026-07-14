using Vela.Core.Source;

namespace Vela.Core.Diagnostics;

/// <summary>A structured compiler message tied to a source span.</summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    TextSpan Span,
    string Message,
    string? Help = null);
