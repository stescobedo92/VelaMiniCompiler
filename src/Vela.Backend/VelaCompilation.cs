using Vela.Core.Diagnostics;
using Vela.Core.Parsing;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>Contains compiler output for one Vela source document.</summary>
public sealed record VelaCompilation(
    SourceText Source,
    ParseResult ParseResult,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? GeneratedSource)
{
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
