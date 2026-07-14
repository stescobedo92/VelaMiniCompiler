using System.Text;
using Vela.Core.Diagnostics;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>One Vela source document participating in a source-linked compilation.</summary>
public sealed record VelaSourceDocument
{
    /// <summary>Validates the source document supplied to a bundle.</summary>
    public VelaSourceDocument(SourceText source, string? packageName = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        PackageName = packageName;
    }

    /// <summary>Gets the physical Vela source document.</summary>
    public SourceText Source { get; }

    /// <summary>Gets the package that contributes this document, when it is a source library.</summary>
    public string? PackageName { get; }
}

/// <summary>Maps a compiler-only concatenation back to the original Vela source documents.</summary>
public sealed class VelaSourceBundle
{
    private readonly IReadOnlyList<Segment> _segments;

    private VelaSourceBundle(SourceText combinedSource, IReadOnlyList<Segment> segments, IReadOnlySet<string> sourcePackages)
    {
        CombinedSource = combinedSource;
        _segments = segments;
        SourcePackages = sourcePackages;
    }

    /// <summary>Gets the internal concatenated source consumed by the parser and emitter.</summary>
    public SourceText CombinedSource { get; }

    /// <summary>Gets the package names that are linked directly as Vela source.</summary>
    public IReadOnlySet<string> SourcePackages { get; }

    /// <summary>Creates a deterministic bundle in dependency order followed by the application source.</summary>
    public static VelaSourceBundle Create(IReadOnlyList<VelaSourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("A Vela source bundle requires at least one document.", nameof(documents));
        }

        var builder = new StringBuilder();
        var segments = new List<Segment>(documents.Count);
        var sourcePackages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var filePath = document.Source.FilePath.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
            builder.Append("// <vela-source: ").Append(filePath).AppendLine(">");
            var start = builder.Length;
            builder.Append(document.Source.Text);
            segments.Add(new Segment(start, document.Source));
            if (!string.IsNullOrWhiteSpace(document.PackageName))
            {
                sourcePackages.Add(document.PackageName);
            }

            if (document.Source.Text.Length == 0 || document.Source.Text[^1] != '\n')
            {
                builder.AppendLine();
            }
        }

        var combined = new SourceText(builder.ToString(), documents[^1].Source.FilePath);
        return new VelaSourceBundle(combined, segments, sourcePackages);
    }

    /// <summary>Resolves an internal span to its original document location.</summary>
    public TextLocation GetLocation(TextSpan span)
    {
        var mapped = GetMappedSpan(span);
        return mapped.Source.GetLocation(mapped.Span);
    }

    /// <summary>Maps a diagnostic to the source document that owns its span.</summary>
    public VelaMappedDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var mapped = GetMappedSpan(diagnostic.Span);
        return new VelaMappedDiagnostic(mapped.Source, diagnostic with { Span = mapped.Span });
    }

    private MappedSpan GetMappedSpan(TextSpan span)
    {
        foreach (var segment in _segments)
        {
            var end = segment.Start + segment.Source.Length;
            if (span.Start < segment.Start || span.Start > end)
            {
                continue;
            }

            var localStart = span.Start - segment.Start;
            var localEnd = Math.Min(segment.Source.Length, checked(localStart + span.Length));
            return new MappedSpan(segment.Source, TextSpan.FromBounds(localStart, localEnd));
        }

        return new MappedSpan(CombinedSource, span);
    }

    private sealed record Segment(int Start, SourceText Source);

    private sealed record MappedSpan(SourceText Source, TextSpan Span);
}

/// <summary>Pairs a diagnostic with the original Vela document used for rendering it.</summary>
public sealed record VelaMappedDiagnostic(SourceText Source, Diagnostic Diagnostic);
