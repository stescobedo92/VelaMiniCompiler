namespace Vela.Core.Lexing;

public enum TokenKind
{
    BadToken,
    EndOfFile,
    NewLine,
    Indent,
    Dedent,

    Identifier,
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,

    LetKeyword,
    VarKeyword,
    FnKeyword,
    RecordKeyword,
    ReturnKeyword,
    IfKeyword,
    ElseKeyword,
    TrueKeyword,
    FalseKeyword,
    NilKeyword,

    Plus,
    Minus,
    Star,
    Slash,
    Equals,
    EqualsEquals,
    BangEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Arrow,
    Colon,
    Comma,
    Dot,
    Question,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket
}
