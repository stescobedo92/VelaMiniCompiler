using System.Globalization;
using System.Text;
using Vela.Core.Diagnostics;
using Vela.Core.Source;

namespace Vela.Core.Lexing;

/// <summary>Converts Vela source into tokens, including indentation boundaries.</summary>
public sealed class VelaLexer
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly List<SyntaxToken> _tokens = [];
    private readonly Stack<int> _indentation = new([0]);
    private int _position;
    private bool _atLineStart = true;

    public VelaLexer(SourceText source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public LexResult Lex()
    {
        while (_position < _source.Length)
        {
            if (_atLineStart)
            {
                ScanIndentation();
                if (_position >= _source.Length)
                {
                    break;
                }
            }

            var start = _position;
            var current = Current;

            if (current is ' ' or '\t')
            {
                _position++;
                continue;
            }

            if (IsLineBreakStart(current))
            {
                ScanNewLine(start);
                continue;
            }

            if (StartsComment())
            {
                SkipComment();
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                ScanIdentifierOrKeyword(start);
                continue;
            }

            if (char.IsDigit(current))
            {
                ScanNumber(start);
                continue;
            }

            if (current == '"')
            {
                ScanString(start);
                continue;
            }

            ScanPunctuation(start);
        }

        while (_indentation.Count > 1)
        {
            _indentation.Pop();
            _tokens.Add(new SyntaxToken(TokenKind.Dedent, new TextSpan(_position, 0), string.Empty));
        }

        _tokens.Add(new SyntaxToken(TokenKind.EndOfFile, new TextSpan(_position, 0), string.Empty));
        return new LexResult(_tokens, _diagnostics.Items.ToArray());
    }

    private char Current => _source[_position];

    private char Peek(int offset) => _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private void ScanIndentation()
    {
        var start = _position;
        var width = 0;

        while (_position < _source.Length && (Current == ' ' || Current == '\t'))
        {
            if (Current == '\t')
            {
                _diagnostics.ReportError(
                    "L003",
                    new TextSpan(_position, 1),
                    "Tabs are not allowed for indentation.",
                    "Use four spaces for each indentation level.");
                width += 4;
            }
            else
            {
                width++;
            }

            _position++;
        }

        if (_position >= _source.Length || IsLineBreakStart(Current) || StartsComment())
        {
            _atLineStart = false;
            return;
        }

        if (width % 4 != 0)
        {
            _diagnostics.ReportError(
                "L004",
                new TextSpan(start, width),
                "Indentation must be a multiple of four spaces.",
                "Align this line with an existing block or use four-space indentation.");
        }

        var currentIndent = _indentation.Peek();
        if (width > currentIndent)
        {
            _indentation.Push(width);
            _tokens.Add(new SyntaxToken(TokenKind.Indent, new TextSpan(start, width), string.Empty));
        }
        else if (width < currentIndent)
        {
            while (_indentation.Count > 1 && width < _indentation.Peek())
            {
                _indentation.Pop();
                _tokens.Add(new SyntaxToken(TokenKind.Dedent, new TextSpan(start, 0), string.Empty));
            }

            if (width != _indentation.Peek())
            {
                _diagnostics.ReportError(
                    "L005",
                    new TextSpan(start, width),
                    "Indentation does not match any enclosing block.",
                    "Use the same indentation as the block you want to continue.");
            }
        }

        _atLineStart = false;
    }

    private void ScanNewLine(int start)
    {
        if (Current == '\r' && Peek(1) == '\n')
        {
            _position += 2;
        }
        else
        {
            _position++;
        }

        _tokens.Add(new SyntaxToken(TokenKind.NewLine, TextSpan.FromBounds(start, _position), _source.Text[start.._position]));
        _atLineStart = true;
    }

    private void SkipComment()
    {
        if (Current == '/' && Peek(1) == '/')
        {
            _position += 2;
        }
        else
        {
            _position++;
        }

        while (_position < _source.Length && !IsLineBreakStart(Current))
        {
            _position++;
        }
    }

    private void ScanIdentifierOrKeyword(int start)
    {
        _position++;
        while (_position < _source.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            _position++;
        }

        var text = _source.Text[start.._position];
        var kind = text switch
        {
            "let" => TokenKind.LetKeyword,
            "var" => TokenKind.VarKeyword,
            "fn" => TokenKind.FnKeyword,
            "record" => TokenKind.RecordKeyword,
            "return" => TokenKind.ReturnKeyword,
            "assert" => TokenKind.AssertKeyword,
            "if" => TokenKind.IfKeyword,
            "else" => TokenKind.ElseKeyword,
            "for" => TokenKind.ForKeyword,
            "in" => TokenKind.InKeyword,
            "true" => TokenKind.TrueKeyword,
            "false" => TokenKind.FalseKeyword,
            "nil" => TokenKind.NilKeyword,
            _ => TokenKind.Identifier
        };

        object? value = kind switch
        {
            TokenKind.TrueKeyword => true,
            TokenKind.FalseKeyword => false,
            TokenKind.NilKeyword => null,
            _ => null
        };

        _tokens.Add(new SyntaxToken(kind, TextSpan.FromBounds(start, _position), text, value));
    }

    private void ScanNumber(int start)
    {
        while (_position < _source.Length && char.IsDigit(Current))
        {
            _position++;
        }

        var isFloat = _position < _source.Length && Current == '.' && char.IsDigit(Peek(1));
        if (isFloat)
        {
            _position++;
            while (_position < _source.Length && char.IsDigit(Current))
            {
                _position++;
            }
        }

        var text = _source.Text[start.._position];
        if (isFloat)
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var floatingValue))
            {
                _diagnostics.ReportError("L006", TextSpan.FromBounds(start, _position), "Floating-point literal is outside the supported range.");
            }

            _tokens.Add(new SyntaxToken(TokenKind.FloatLiteral, TextSpan.FromBounds(start, _position), text, floatingValue));
            return;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integerValue))
        {
            _diagnostics.ReportError("L007", TextSpan.FromBounds(start, _position), "Integer literal is outside the supported range.");
        }

        _tokens.Add(new SyntaxToken(TokenKind.IntegerLiteral, TextSpan.FromBounds(start, _position), text, integerValue));
    }

    private void ScanString(int start)
    {
        var builder = new StringBuilder();
        _position++;
        var terminated = false;

        while (_position < _source.Length && !IsLineBreakStart(Current))
        {
            if (Current == '"')
            {
                _position++;
                terminated = true;
                break;
            }

            if (Current == '\\')
            {
                var escapeStart = _position;
                _position++;
                if (_position >= _source.Length || IsLineBreakStart(Current))
                {
                    break;
                }

                var escaped = Current;
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => ReportInvalidEscape(escapeStart, escaped)
                });
                _position++;
                continue;
            }

            builder.Append(Current);
            _position++;
        }

        if (!terminated)
        {
            _diagnostics.ReportError(
                "L002",
                TextSpan.FromBounds(start, _position),
                "Unterminated string literal.",
                "Close the string with a double quote before the end of the line.");
        }

        _tokens.Add(new SyntaxToken(TokenKind.StringLiteral, TextSpan.FromBounds(start, _position), _source.Text[start.._position], builder.ToString()));
    }

    private char ReportInvalidEscape(int start, char escaped)
    {
        _diagnostics.ReportError(
            "L008",
            new TextSpan(start, 2),
            $"Invalid escape sequence '\\{escaped}'.",
            "Use \\n, \\r, \\t, \\\\, or \\\".");
        return escaped;
    }

    private void ScanPunctuation(int start)
    {
        var (kind, width) = Current switch
        {
            '+' => (TokenKind.Plus, 1),
            '-' when Peek(1) == '>' => (TokenKind.Arrow, 2),
            '-' => (TokenKind.Minus, 1),
            '*' => (TokenKind.Star, 1),
            '/' => (TokenKind.Slash, 1),
            '=' when Peek(1) == '=' => (TokenKind.EqualsEquals, 2),
            '=' => (TokenKind.Equals, 1),
            '!' when Peek(1) == '=' => (TokenKind.BangEquals, 2),
            '<' when Peek(1) == '=' => (TokenKind.LessOrEqual, 2),
            '<' => (TokenKind.Less, 1),
            '>' when Peek(1) == '=' => (TokenKind.GreaterOrEqual, 2),
            '>' => (TokenKind.Greater, 1),
            ':' => (TokenKind.Colon, 1),
            ',' => (TokenKind.Comma, 1),
            '.' => (TokenKind.Dot, 1),
            '?' => (TokenKind.Question, 1),
            '(' => (TokenKind.LeftParen, 1),
            ')' => (TokenKind.RightParen, 1),
            '[' => (TokenKind.LeftBracket, 1),
            ']' => (TokenKind.RightBracket, 1),
            _ => (TokenKind.BadToken, 1)
        };

        _position += width;
        var span = TextSpan.FromBounds(start, _position);
        var text = _source.Text[start.._position];
        if (kind == TokenKind.BadToken)
        {
            _diagnostics.ReportError("L001", span, $"Unexpected character '{text}'.");
        }

        _tokens.Add(new SyntaxToken(kind, span, text));
    }

    private bool StartsComment() => Current == '#' || (Current == '/' && Peek(1) == '/');

    private static bool IsLineBreakStart(char character) => character is '\r' or '\n';
}
