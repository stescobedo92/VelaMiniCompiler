using Vela.Backend;
using Vela.Core.Diagnostics;
using Vela.Core.Source;

namespace Vela.LanguageServer;

/// <summary>Tracks open Vela documents and recompiles them on change.</summary>
public sealed class VelaWorkspace
{
    private readonly Dictionary<string, DocumentState> _documents = new(StringComparer.Ordinal);

    public void OpenDocument(string uri, string text, int version)
        => _documents[uri] = new DocumentState(uri, text, version);

    public void ChangeDocument(string uri, string text, int version)
    {
        if (_documents.ContainsKey(uri))
        {
            _documents[uri] = new DocumentState(uri, text, version);
        }
    }

    public void CloseDocument(string uri) => _documents.Remove(uri);

    public bool TryGetDocument(string uri, out DocumentState document) => _documents.TryGetValue(uri, out document!);

    public VelaCompilation CompileDocument(string uri)
    {
        if (!_documents.TryGetValue(uri, out var document))
        {
            throw new InvalidOperationException($"Document '{uri}' is not open.");
        }

        var source = new SourceText(document.Text, DocumentPath.FromUri(uri));
        return VelaCompiler.Compile(source);
    }

    public static VelaCompilation CompileFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var source = new SourceText(File.ReadAllText(fullPath), fullPath);
        return VelaCompiler.Compile(source);
    }

    public static IReadOnlyList<DiagnosticReport> BuildDiagnosticReports(VelaCompilation compilation)
    {
        var reports = new List<DiagnosticReport>(compilation.Diagnostics.Count);
        foreach (var diagnostic in compilation.Diagnostics)
        {
            var mapped = compilation.MapDiagnostic(diagnostic);
            reports.Add(DiagnosticReport.FromMapped(mapped));
        }

        return reports;
    }
}

public sealed record DocumentState(string Uri, string Text, int Version);

public sealed record DiagnosticReport(
    string Code,
    string Severity,
    string Message,
    string File,
    int Line,
    int Column,
    int EndLine,
    int EndColumn)
{
    public static DiagnosticReport FromMapped(VelaMappedDiagnostic mapped)
    {
        var start = mapped.Source.GetLocation(mapped.Diagnostic.Span.Start);
        var end = mapped.Source.GetLocation(Math.Max(mapped.Diagnostic.Span.Start, mapped.Diagnostic.Span.End));
        var severity = mapped.Diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return new DiagnosticReport(
            mapped.Diagnostic.Code,
            severity,
            mapped.Diagnostic.Message,
            mapped.Source.FilePath,
            start.Line,
            start.Column,
            end.Line,
            end.Column);
    }
}

internal static class DocumentPath
{
    public static string FromUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            return parsed.LocalPath;
        }

        return uri;
    }
}
