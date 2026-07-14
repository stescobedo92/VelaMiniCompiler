using Vela.Core.Diagnostics;
using Vela.Core.Syntax;

namespace Vela.Core.Parsing;

public sealed record ParseResult(CompilationUnitSyntax Root, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
