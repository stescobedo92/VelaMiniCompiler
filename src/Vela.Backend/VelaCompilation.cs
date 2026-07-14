using Vela.Core.Diagnostics;
using Vela.Core.Parsing;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>Contains compiler output for one Vela source document.</summary>
public sealed record VelaCompilation(
    SourceText Source,
    ParseResult ParseResult,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? GeneratedSource,
    VelaSourceBundle? SourceBundle = null)
{
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Gets a diagnostic with the physical source document that owns its location.</summary>
    public VelaMappedDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return SourceBundle?.MapDiagnostic(diagnostic) ?? new VelaMappedDiagnostic(Source, diagnostic);
    }
}
