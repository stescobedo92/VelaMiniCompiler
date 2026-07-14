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
    private readonly List<SyntaxTrivia> _trivia = [];
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
                SkipComment(start);
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

        _tokens.Add(new SyntaxToken(TokenKind.EndOfFile, new TextSpan(_position, 0), string.Empty));
        return new LexResult(_tokens, _diagnostics.Items.ToArray(), _trivia.ToArray());
    }

    private char Current => _source[_position];

    private char Peek(int offset) => _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private void ScanIndentation()
    {
        while (_position < _source.Length && (Current == ' ' || Current == '\t'))
        {
            _position++;
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

    private void SkipComment(int start)
    {
        if (Current == '#')
        {
            _position++;
            while (_position < _source.Length && !IsLineBreakStart(Current))
            {
                _position++;
            }

            _trivia.Add(new SyntaxTrivia(SyntaxTriviaKind.LineComment, TextSpan.FromBounds(start, _position), _source.Text[start.._position]));
            return;
        }

        if (Current == '/' && Peek(1) == '/')
        {
            var isDocumentation = Peek(2) == '/';
            _position += 2;
            while (_position < _source.Length && !IsLineBreakStart(Current))
            {
                _position++;
            }

            _trivia.Add(new SyntaxTrivia(
                isDocumentation ? SyntaxTriviaKind.DocumentationLineComment : SyntaxTriviaKind.LineComment,
                TextSpan.FromBounds(start, _position),
                _source.Text[start.._position]));
            return;
        }

        var documentationBlock = Peek(2) == '*';
        _position += 2;
        var depth = 1;
        while (_position < _source.Length && depth > 0)
        {
            if (Current == '/' && Peek(1) == '*')
            {
                depth++;
                _position += 2;
                continue;
            }

            if (Current == '*' && Peek(1) == '/')
            {
                depth--;
                _position += 2;
                continue;
            }

            _position++;
        }

        var span = TextSpan.FromBounds(start, _position);
        if (depth != 0)
        {
            _diagnostics.ReportError("L009", span, "Unterminated block comment.", "Close the comment with '*/'.");
        }

        _trivia.Add(new SyntaxTrivia(
            documentationBlock ? SyntaxTriviaKind.DocumentationBlockComment : SyntaxTriviaKind.BlockComment,
            span,
            _source.Text[start.._position]));
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
            "async" => TokenKind.AsyncKeyword,
            "await" => TokenKind.AwaitKeyword,
            "fn" => TokenKind.FnKeyword,
            "record" => TokenKind.RecordKeyword,
            "enum" => TokenKind.EnumKeyword,
            "include" => TokenKind.IncludeKeyword,
            "public" => TokenKind.PublicKeyword,
            "ffi" => TokenKind.FfiKeyword,
            "class" => TokenKind.ClassKeyword,
            "struct" => TokenKind.StructKeyword,
            "interface" => TokenKind.InterfaceKeyword,
            "implements" => TokenKind.ImplementsKeyword,
            "as" => TokenKind.AsKeyword,
            "return" => TokenKind.ReturnKeyword,
            "assert" => TokenKind.AssertKeyword,
            "defer" => TokenKind.DeferKeyword,
            "try" => TokenKind.TryKeyword,
            "catch" => TokenKind.CatchKeyword,
            "finally" => TokenKind.FinallyKeyword,
            "if" => TokenKind.IfKeyword,
            "else" => TokenKind.ElseKeyword,
            "for" => TokenKind.ForKeyword,
            "in" => TokenKind.InKeyword,
            "while" => TokenKind.WhileKeyword,
            "break" => TokenKind.BreakKeyword,
            "continue" => TokenKind.ContinueKeyword,
            "switch" => TokenKind.SwitchKeyword,
            "case" => TokenKind.CaseKeyword,
            "default" => TokenKind.DefaultKeyword,
            "true" => TokenKind.TrueKeyword,
            "false" => TokenKind.FalseKeyword,
            "nil" or "null" => TokenKind.NilKeyword,
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
            ';' => (TokenKind.Semicolon, 1),
            '.' => (TokenKind.Dot, 1),
            '?' => (TokenKind.Question, 1),
            '(' => (TokenKind.LeftParen, 1),
            ')' => (TokenKind.RightParen, 1),
            '[' => (TokenKind.LeftBracket, 1),
            ']' => (TokenKind.RightBracket, 1),
            '{' => (TokenKind.LeftBrace, 1),
            '}' => (TokenKind.RightBrace, 1),
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

    private bool StartsComment() => Current == '#'
        || (Current == '/' && Peek(1) is '/' or '*');

    private static bool IsLineBreakStart(char character) => character is '\r' or '\n';
}
