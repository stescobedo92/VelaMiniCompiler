using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Backend;

/// <summary>Accumulates the accepted chunks of one interactive session.</summary>
/// <remarks>
/// The session only stores text: callers compile <see cref="Source"/> after
/// every submission and roll the chunk back with <see cref="Pop"/> when the
/// combined source no longer compiles. Every accepted chunk compiled cleanly,
/// so the buffer only grows with valid programs.
/// </remarks>
public sealed class VelaReplSession
{
    private readonly List<string> _chunks = [];

    /// <summary>Gets the number of accepted chunks.</summary>
    public int Count => _chunks.Count;

    /// <summary>Gets the combined source of all accepted chunks.</summary>
    public string Source => _chunks.Count == 0 ? string.Empty : string.Join('\n', _chunks) + '\n';

    /// <summary>Accepts <paramref name="chunk"/> into the session.</summary>
    public void Push(string chunk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk);
        _chunks.Add(chunk);
    }

    /// <summary>Removes the most recently accepted chunk. Returns false when the session is empty.</summary>
    public bool Pop()
    {
        if (_chunks.Count == 0)
        {
            return false;
        }

        _chunks.RemoveAt(_chunks.Count - 1);
        return true;
    }

    /// <summary>Discards every accepted chunk.</summary>
    public void Clear() => _chunks.Clear();

    /// <summary>Gets whether <paramref name="text"/> leaves braces unbalanced and needs continuation.</summary>
    /// <remarks>
    /// Balance is counted over tokens, so braces inside strings and comments
    /// never count. Only surplus opens continue; surplus closes are reported
    /// by the compiler instead.
    /// </remarks>
    public static bool ExpectsMore(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var depth = 0;
        foreach (var token in new VelaLexer(new SourceText(text, "<repl>")).Lex().Tokens)
        {
            if (token.Kind == TokenKind.LeftBrace)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.RightBrace)
            {
                depth--;
            }
        }

        return depth > 0;
    }
}
