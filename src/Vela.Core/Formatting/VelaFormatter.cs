using System.Text;
using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.Core.Formatting;

/// <summary>Contains the formatted text of one Vela source document.</summary>
/// <param name="Text">The formatted source, or the original text when formatting was refused.</param>
/// <param name="Diagnostics">Lexical diagnostics encountered while formatting.</param>
public sealed record FormatResult(string Text, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Gets whether formatting was refused because the source contains lexical errors.</summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Formats Vela source directly from its token stream.
/// </summary>
/// <remarks>
/// The formatter never parses: it re-emits tokens with normalized spacing and
/// brace-driven indentation while preserving every comment and literal verbatim.
/// Statements always end with <c>;</c>, so newlines inserted after statement
/// boundaries cannot change the program; constructs that must stay on one line
/// (such as record destructuring patterns) are detected by brace matching and
/// kept inline instead of split.
/// The single deliberate exception is <c>&lt;</c>/<c>&gt;</c>, which double as
/// generic brackets: their original spacing is preserved instead of guessed.
/// A brace group whose closing brace continues an expression (record patterns
/// such as <c>let Point { x, y } = point;</c>, call arguments) is kept on one
/// line; only standalone blocks indent onto new lines.
/// Formatting is idempotent: formatting formatted output returns it unchanged.
/// </remarks>
public static class VelaFormatter
{
    private const int IndentWidth = 4;

    /// <summary>Formats <paramref name="source"/> with the canonical Vela layout.</summary>
    public static FormatResult Format(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lexed = new VelaLexer(source).Lex();
        if (lexed.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return new FormatResult(source.Text, lexed.Diagnostics);
        }

        var emitter = new Emitter(CollectItems(lexed));
        return new FormatResult(emitter.Run(), lexed.Diagnostics);
    }

    private enum ItemKind
    {
        Token,
        LineComment,
        BlockComment,
    }

    private readonly record struct FormatItem(
        ItemKind Kind,
        TokenKind TokenKind,
        string Text,
        int Start,
        int End,
        bool Multiline);

    private static List<FormatItem> CollectItems(LexResult lexed)
    {
        var items = new List<FormatItem>(lexed.Tokens.Count + lexed.Trivia.Count);
        foreach (var token in lexed.Tokens)
        {
            if (token.Kind == TokenKind.EndOfFile)
            {
                continue;
            }

            items.Add(new FormatItem(
                ItemKind.Token,
                token.Kind,
                token.Kind == TokenKind.NewLine ? "\n" : token.Text,
                token.Span.Start,
                token.Span.End,
                false));
        }

        foreach (var trivia in lexed.Trivia)
        {
            var kind = trivia.Kind is SyntaxTriviaKind.LineComment or SyntaxTriviaKind.DocumentationLineComment
                ? ItemKind.LineComment
                : ItemKind.BlockComment;
            var multiline = kind == ItemKind.BlockComment && trivia.Text.IndexOfAny(['\r', '\n']) >= 0;
            items.Add(new FormatItem(kind, TokenKind.BadToken, trivia.Text, trivia.Span.Start, trivia.Span.End, multiline));
        }

        // Token and trivia spans never overlap, so ordering by start is total.
        items.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return items;
    }

    private sealed class Emitter(List<FormatItem> items)
    {
        private readonly StringBuilder _output = new(items.Sum(static item => item.Text.Length) + 16);
        private readonly Dictionary<int, bool> _inlineOpens = ComputeInlineOpens(items);
        private readonly Stack<bool> _braceStack = new();
        private int _indent;
        private int _inlineDepth;
        private bool _atLineStart = true;
        private bool _blankPending;
        private bool _trailingSpace;
        private TokenKind? _previousToken;
        private int _lastEnd;

        public string Run()
        {
            for (var index = 0; index < items.Count; index++)
            {
                Emit(items[index], index);
                _lastEnd = items[index].End;
            }

            if (_output.Length == 0)
            {
                return string.Empty;
            }

            if (_output[^1] != '\n')
            {
                _output.Append('\n');
            }

            return _output.ToString();
        }

        private void Emit(FormatItem item, int index)
        {
            if (item.Kind != ItemKind.Token)
            {
                EmitComment(item, index);
                return;
            }

            switch (item.TokenKind)
            {
                case TokenKind.NewLine:
                    if (_inlineDepth > 0)
                    {
                        if (!_atLineStart)
                        {
                            AppendTrailingSpace();
                        }
                    }
                    else
                    {
                        Break();
                    }

                    return;
                case TokenKind.LeftBrace:
                    EmitOpenBrace(index);
                    return;
                case TokenKind.RightBrace:
                    EmitCloseBrace(index);
                    return;
                case TokenKind.Semicolon:
                    WriteContent(";", spaceBefore: false);
                    _previousToken = TokenKind.Semicolon;
                    if (Peek(index) is { Kind: ItemKind.Token } next && next.TokenKind != TokenKind.NewLine)
                    {
                        if (_inlineDepth == 0)
                        {
                            Break();
                        }
                        else
                        {
                            AppendTrailingSpace();
                        }
                    }

                    return;
                case TokenKind.Comma:
                    WriteContent(",", spaceBefore: false);
                    _previousToken = TokenKind.Comma;
                    CommaOrColonAfter(index, attachesTo: null);
                    return;
                case TokenKind.Colon:
                    WriteContent(":", spaceBefore: false);
                    _previousToken = TokenKind.Colon;
                    CommaOrColonAfter(index, attachesTo: TokenKind.Colon);
                    return;
                case TokenKind.Dot:
                    WriteContent(".", spaceBefore: false);
                    _previousToken = TokenKind.Dot;
                    return;
                case TokenKind.LeftParen:
                    WriteContent("(", spaceBefore: _previousToken is TokenKind previous && IsKeyword(previous));
                    _previousToken = TokenKind.LeftParen;
                    return;
                case TokenKind.LeftBracket:
                    WriteContent("[", spaceBefore: _previousToken is TokenKind bracketPrevious && IsKeyword(bracketPrevious));
                    _previousToken = TokenKind.LeftBracket;
                    return;
                case TokenKind.RightParen:
                case TokenKind.RightBracket:
                    WriteContent(item.Text, spaceBefore: false);
                    _previousToken = item.TokenKind;
                    CloserAfter(index);
                    return;
                case TokenKind.Question:
                    WriteContent("?", spaceBefore: false);
                    _previousToken = TokenKind.Question;
                    CloserAfter(index);
                    return;
                case TokenKind.At:
                    WriteContent("@", spaceBefore: IsWordOrCloser(_previousToken));
                    _previousToken = TokenKind.At;
                    return;
                case TokenKind.Less:
                case TokenKind.Greater:
                    // Angle brackets double as generic brackets: keep the author's spacing.
                    WriteContent(item.Text, spaceBefore: !_atLineStart && item.Start > _lastEnd);
                    _previousToken = item.TokenKind;
                    AngleAfter(item, index);
                    return;
                case TokenKind.Minus:
                case TokenKind.Plus:
                    if (IsUnaryContext(_previousToken))
                    {
                        WriteContent(item.Text, spaceBefore: IsWordOrCloser(_previousToken));
                        _previousToken = item.TokenKind;
                        return;
                    }

                    EmitBinary(item, index);
                    return;
                default:
                    if (IsWord(item.TokenKind))
                    {
                        WriteContent(item.Text, spaceBefore: IsWordOrCloser(_previousToken));
                        _previousToken = item.TokenKind;
                        return;
                    }

                    EmitBinary(item, index);
                    return;
            }
        }

        private void EmitBinary(FormatItem item, int index)
        {
            WriteContent(item.Text, spaceBefore: !_atLineStart);
            _previousToken = item.TokenKind;
            if (Peek(index) is { Kind: ItemKind.Token } next && next.TokenKind != TokenKind.NewLine)
            {
                AppendTrailingSpace();
            }
        }

        private void EmitOpenBrace(int index)
        {
            // A brace group that continues an expression (record patterns, call arguments)
            // stays on one line; only standalone blocks indent onto new lines.
            var inline = _inlineDepth > 0 || (_inlineOpens.TryGetValue(index, out var inlineOpen) && inlineOpen);
            _braceStack.Push(inline);
            if (inline)
            {
                _inlineDepth++;
                WriteContent("{", spaceBefore: !_atLineStart && _previousToken is not (TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.LeftBrace));
                _previousToken = TokenKind.LeftBrace;
                if (Peek(index) is { Kind: ItemKind.Token } after
                    && after.TokenKind != TokenKind.NewLine
                    && after.TokenKind != TokenKind.RightBrace)
                {
                    AppendTrailingSpace();
                }

                return;
            }

            WriteContent("{", spaceBefore: !_atLineStart && _previousToken is not (TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.LeftBrace));
            _previousToken = TokenKind.LeftBrace;
            _indent++;

            if (NextSignificant(index) is { Kind: ItemKind.Token, TokenKind: TokenKind.RightBrace })
            {
                return;
            }

            if (Peek(index) is { Kind: ItemKind.Token } next && next.TokenKind != TokenKind.NewLine)
            {
                Break();
            }
        }

        private void EmitCloseBrace(int index)
        {
            var inline = _braceStack.Count > 0 && _braceStack.Pop();
            if (inline)
            {
                _inlineDepth = Math.Max(0, _inlineDepth - 1);
                if (_previousToken != TokenKind.LeftBrace && !_atLineStart)
                {
                    AppendTrailingSpace();
                }

                WriteContent("}", spaceBefore: false);
                _previousToken = TokenKind.RightBrace;
                return;
            }

            _indent = Math.Max(0, _indent - 1);
            var emptyBlock = _previousToken == TokenKind.LeftBrace && _output.Length > 0 && _output[^1] == '{';
            if (!emptyBlock && !_atLineStart)
            {
                Break();
            }

            WriteContent("}", spaceBefore: false);
            _previousToken = TokenKind.RightBrace;

            // Join `} else {` / `} catch {` / `} finally {`; everything else starts a line,
            // unless the source already breaks there (the NewLine item breaks for us).
            if (Peek(index) is { Kind: ItemKind.Token } next
                && next.TokenKind != TokenKind.NewLine
                && next.TokenKind is not (TokenKind.ElseKeyword or TokenKind.CatchKeyword or TokenKind.FinallyKeyword
                    or TokenKind.Semicolon or TokenKind.Comma or TokenKind.RightParen or TokenKind.RightBracket
                    or TokenKind.Dot or TokenKind.Question))
            {
                Break();
            }
        }

        private void EmitComment(FormatItem item, int index)
        {
            if (item is { Kind: ItemKind.BlockComment, Multiline: true })
            {
                if (!_atLineStart)
                {
                    Break();
                }

                WriteContent(item.Text, spaceBefore: false);
                if (Peek(index) is { } following && !(following.Kind == ItemKind.Token && following.TokenKind == TokenKind.NewLine))
                {
                    Break();
                }

                return;
            }

            WriteContent(item.Text, spaceBefore: !_atLineStart && _previousToken is not (TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.LeftBrace or TokenKind.At or TokenKind.Dot));
            if (Peek(index) is { Kind: ItemKind.Token } next
                && next.TokenKind != TokenKind.NewLine
                && next.TokenKind is not (TokenKind.Comma or TokenKind.Semicolon or TokenKind.Colon
                    or TokenKind.RightParen or TokenKind.RightBracket or TokenKind.RightBrace
                    or TokenKind.Dot or TokenKind.Question))
            {
                // Single-line block comment followed by code on the same line.
                AppendTrailingSpace();
            }
        }

        private void CommaOrColonAfter(int index, TokenKind? attachesTo)
        {
            if (Peek(index) is not { Kind: ItemKind.Token } next || next.TokenKind == TokenKind.NewLine)
            {
                return;
            }

            if (next.TokenKind is TokenKind.RightParen or TokenKind.RightBracket or TokenKind.RightBrace
                || (attachesTo == TokenKind.Colon && next.TokenKind is TokenKind.Comma or TokenKind.Semicolon or TokenKind.Colon))
            {
                return;
            }

            AppendTrailingSpace();
        }

        private void CloserAfter(int index)
        {
            if (Peek(index) is not { Kind: ItemKind.Token } next || next.TokenKind == TokenKind.NewLine)
            {
                return;
            }

            if (IsWord(next.TokenKind) || next.TokenKind == TokenKind.LeftBrace)
            {
                AppendTrailingSpace();
            }
        }

        private void AngleAfter(FormatItem item, int index)
        {
            if (Peek(index) is not { Kind: ItemKind.Token } next || next.TokenKind == TokenKind.NewLine)
            {
                return;
            }

            if (next.Start > item.End)
            {
                AppendTrailingSpace();
            }
        }

        private void AppendTrailingSpace()
        {
            // The next content already supplies its own spacing (operators, words after
            // closers, gap-kept angles), so a pending trailing space suppresses it once.
            if (!_trailingSpace)
            {
                _output.Append(' ');
                _trailingSpace = true;
            }
        }

        private FormatItem? Peek(int index) => index + 1 < items.Count ? items[index + 1] : null;

        private FormatItem? NextSignificant(int index) => NextSignificantAfter(items, index);

        private static FormatItem? NextSignificantAfter(List<FormatItem> candidates, int index)
        {
            for (var next = index + 1; next < candidates.Count; next++)
            {
                var candidate = candidates[next];
                if (candidate.Kind == ItemKind.Token && candidate.TokenKind == TokenKind.NewLine)
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static Dictionary<int, bool> ComputeInlineOpens(List<FormatItem> candidates)
        {
            var inline = new Dictionary<int, bool>();
            var stack = new Stack<int>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var item = candidates[index];
                if (item.Kind != ItemKind.Token)
                {
                    continue;
                }

                if (item.TokenKind == TokenKind.LeftBrace)
                {
                    stack.Push(index);
                }
                else if (item.TokenKind == TokenKind.RightBrace && stack.Count > 0)
                {
                    var open = stack.Pop();
                    if (NextSignificantAfter(candidates, index) is { } follow && IsInlineContinuation(follow))
                    {
                        inline[open] = true;
                    }
                }
            }

            return inline;
        }

        private static bool IsInlineContinuation(FormatItem item) =>
            item.Kind == ItemKind.Token && item.TokenKind is TokenKind.Equals or TokenKind.Comma
                or TokenKind.RightParen or TokenKind.RightBracket or TokenKind.Dot or TokenKind.Question
                or TokenKind.Arrow or TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash
                or TokenKind.EqualsEquals or TokenKind.BangEquals or TokenKind.LessOrEqual or TokenKind.GreaterOrEqual;

        private void Break()
        {
            _trailingSpace = false;
            if (_output.Length == 0)
            {
                return;
            }

            if (_output[^1] != '\n')
            {
                _output.Append('\n');
            }
            else
            {
                _blankPending = true;
            }

            _atLineStart = true;
        }

        private void WriteContent(string text, bool spaceBefore)
        {
            if (_atLineStart)
            {
                if (_blankPending)
                {
                    _output.Append('\n');
                    _blankPending = false;
                }

                _output.Append(' ', _indent * IndentWidth);
                _atLineStart = false;
            }
            else if (spaceBefore && !_trailingSpace)
            {
                _output.Append(' ');
            }

            _trailingSpace = false;
            _output.Append(text);
        }

        private static bool IsWord(TokenKind kind) =>
            kind is TokenKind.Identifier or TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.StringLiteral
            || (kind >= TokenKind.LetKeyword && kind <= TokenKind.NilKeyword);

        private static bool IsKeyword(TokenKind kind) => kind >= TokenKind.LetKeyword && kind <= TokenKind.NilKeyword;

        private static bool IsWordOrCloser(TokenKind? kind) =>
            kind is TokenKind word && (IsWord(word) || word is TokenKind.RightParen or TokenKind.RightBracket or TokenKind.RightBrace or TokenKind.Question);

        private static bool IsUnaryContext(TokenKind? previous) =>
            previous is null
                or TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.LeftBrace
                or TokenKind.Comma or TokenKind.Semicolon or TokenKind.Colon or TokenKind.Arrow
                or TokenKind.Equals or TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash
                or TokenKind.EqualsEquals or TokenKind.BangEquals
                or TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater or TokenKind.GreaterOrEqual
                or TokenKind.ReturnKeyword or TokenKind.AwaitKeyword;
    }
}
