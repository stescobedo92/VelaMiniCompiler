using System.Collections.Immutable;

namespace Vela.Core.Source;

/// <summary>Immutable source content together with its line index.</summary>
public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    public SourceText(string text, string filePath = "<memory>")
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        FilePath = string.IsNullOrWhiteSpace(filePath) ? "<memory>" : filePath;
        _lineStarts = BuildLineStarts(text);
    }

    public string Text { get; }

    public string FilePath { get; }

    public int Length => Text.Length;

    public int LineCount => _lineStarts.Length;

    public char this[int position] => Text[position];

    public TextLocation GetLocation(TextSpan span) => GetLocation(span.Start);

    public TextLocation GetLocation(int position)
    {
        if (position < 0 || position > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var lineIndex = GetLineIndex(position);
        return new TextLocation(FilePath, lineIndex + 1, position - _lineStarts[lineIndex] + 1);
    }

    public int GetLineIndex(int position)
    {
        if (position < 0 || position > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var low = 0;
        var high = _lineStarts.Length - 1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_lineStarts[middle] <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Math.Max(0, high);
    }

    public TextSpan GetLineSpan(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(lineIndex));
        }

        var start = _lineStarts[lineIndex];
        var end = lineIndex + 1 < _lineStarts.Length ? _lineStarts[lineIndex + 1] : Length;

        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r'))
        {
            end--;
        }

        return TextSpan.FromBounds(start, end);
    }

    public string GetLineText(int lineIndex)
    {
        var span = GetLineSpan(lineIndex);
        return Text.Substring(span.Start, span.Length);
    }

    private static ImmutableArray<int> BuildLineStarts(string text)
    {
        var starts = ImmutableArray.CreateBuilder<int>();
        starts.Add(0);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts.ToImmutable();
    }
}
