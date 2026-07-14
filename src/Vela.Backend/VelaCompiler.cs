using Vela.Core.Diagnostics;
using Vela.Core.Parsing;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>Compiles Vela source into deterministic C# source code.</summary>
public static class VelaCompiler
{
    public static VelaCompilation Compile(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parseResult = VelaParser.Parse(source);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(parseResult.Diagnostics);
        if (diagnostics.HasErrors)
        {
            return new VelaCompilation(source, parseResult, diagnostics.Items.ToArray(), null);
        }

        var generatedSource = new CSharpEmitter(source, diagnostics).Emit(parseResult.Root);
        return new VelaCompilation(
            source,
            parseResult,
            diagnostics.Items.ToArray(),
            diagnostics.HasErrors ? null : generatedSource);
    }
}
