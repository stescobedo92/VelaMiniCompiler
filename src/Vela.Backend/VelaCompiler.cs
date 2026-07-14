using Vela.Core.Diagnostics;
using Vela.Core.Parsing;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>Compiles Vela source into deterministic C# source code.</summary>
public static class VelaCompiler
{
    public static VelaCompilation Compile(SourceText source)
        => Compile(source, []);

    /// <summary>Compiles Vela source while binding the supplied locked native package manifests.</summary>
    public static VelaCompilation Compile(SourceText source, IReadOnlyList<VelaLibraryImport> imports)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(imports);

        var parseResult = VelaParser.Parse(source);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(parseResult.Diagnostics);
        if (diagnostics.HasErrors)
        {
            return new VelaCompilation(source, parseResult, diagnostics.Items.ToArray(), null);
        }

        var generatedSource = new CSharpEmitter(source, diagnostics, imports).Emit(parseResult.Root);
        return new VelaCompilation(
            source,
            parseResult,
            diagnostics.Items.ToArray(),
            diagnostics.HasErrors ? null : generatedSource);
    }

    /// <summary>Compiles one Vela library source document and records its native FFI exports.</summary>
    public static VelaLibraryCompilation CompileLibrary(SourceText source, string packageName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var parseResult = VelaParser.Parse(source);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(parseResult.Diagnostics);
        if (diagnostics.HasErrors)
        {
            var compilation = new VelaCompilation(source, parseResult, diagnostics.Items.ToArray(), null);
            return new VelaLibraryCompilation(compilation, []);
        }

        var emission = new CSharpEmitter(source, diagnostics).EmitLibrary(parseResult.Root, packageName);
        var result = new VelaCompilation(
            source,
            parseResult,
            diagnostics.Items.ToArray(),
            diagnostics.HasErrors ? null : emission.GeneratedSource);
        return new VelaLibraryCompilation(result, diagnostics.HasErrors ? [] : emission.Exports);
    }
}

/// <summary>Contains the compilation and ABI export surface of a Vela library source file.</summary>
public sealed record VelaLibraryCompilation(VelaCompilation Compilation, IReadOnlyList<VelaFfiExport> Exports);
