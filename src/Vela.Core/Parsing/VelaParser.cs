using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Core.Parsing;

/// <summary>Builds a syntax tree from Vela tokens while recovering from independent errors.</summary>
public sealed class VelaParser
{
    private readonly IReadOnlyList<SyntaxToken> _tokens;
    private readonly DiagnosticBag _diagnostics = new();
    private int _position;

    public VelaParser(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lexResult = new VelaLexer(source).Lex();
        _tokens = lexResult.Tokens;
        _diagnostics.AddRange(lexResult.Diagnostics);
    }

    public static ParseResult Parse(SourceText source) => new VelaParser(source).ParseCompilationUnit();

    public ParseResult ParseCompilationUnit()
    {
        var members = new List<StatementSyntax>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind == TokenKind.EndOfFile)
            {
                break;
            }

            members.Add(ParseStatement());
        }

        var endOfFile = Match(TokenKind.EndOfFile);
        return new ParseResult(new CompilationUnitSyntax(members, endOfFile), _diagnostics.Items.ToArray());
    }

    private StatementSyntax ParseStatement() => Current.Kind switch
    {
        TokenKind.IncludeKeyword => ParseIncludeDirective(),
        TokenKind.PublicKeyword => ParsePublicDeclaration(),
        TokenKind.LetKeyword => ParseLetStatement(),
        TokenKind.VarKeyword => ParseVarStatement(),
        TokenKind.FnKeyword => ParseFunctionDeclaration(),
        TokenKind.RecordKeyword => ParseRecordDeclaration(),
        TokenKind.ClassKeyword or TokenKind.StructKeyword or TokenKind.InterfaceKeyword => ParseObjectDeclaration(null),
        TokenKind.ReturnKeyword => ParseReturnStatement(),
        TokenKind.AssertKeyword => ParseAssertStatement(),
        TokenKind.IfKeyword => ParseIfStatement(),
        TokenKind.ForKeyword => ParseForStatement(),
        _ => ParseExpressionStatement()
    };

    private StatementSyntax ParsePublicDeclaration()
    {
        var publicKeyword = Match(TokenKind.PublicKeyword);
        if (Current.Kind == TokenKind.FfiKeyword || Current.Kind == TokenKind.FnKeyword)
        {
            var ffiKeyword = Current.Kind == TokenKind.FfiKeyword ? NextToken() : null;
            return ParseFunctionDeclaration(publicKeyword, ffiKeyword);
        }

        if (Current.Kind is TokenKind.ClassKeyword or TokenKind.StructKeyword or TokenKind.InterfaceKeyword)
        {
            return ParseObjectDeclaration(publicKeyword);
        }

        _diagnostics.ReportError(
            "P007",
            Current.Span,
            "The 'public' modifier must declare a function, class, struct, or interface.",
            "Use 'public fn', 'public ffi fn', 'public class', 'public struct', or 'public interface'.");
        SynchronizeToStatementBoundary();
        return new ExpressionStatementSyntax(new LiteralExpressionSyntax(SyntaxToken.Missing(TokenKind.Identifier, publicKeyword.Span.End)));
    }

    private IncludeDirectiveSyntax ParseIncludeDirective()
    {
        var include = Match(TokenKind.IncludeKeyword);
        var segments = new List<SyntaxToken> { Match(TokenKind.Identifier) };
        var dots = new List<SyntaxToken>();
        while (Current.Kind == TokenKind.Dot)
        {
            dots.Add(NextToken());
            segments.Add(Match(TokenKind.Identifier));
        }

        SyntaxToken? asKeyword = null;
        SyntaxToken? alias = null;
        if (Current.Kind == TokenKind.AsKeyword)
        {
            asKeyword = NextToken();
            alias = Match(TokenKind.Identifier);
        }

        ConsumeStatementTerminator();
        return new IncludeDirectiveSyntax(include, segments, dots, asKeyword, alias);
    }

    private LetStatementSyntax ParseLetStatement()
    {
        var keyword = Match(TokenKind.LetKeyword);
        var identifier = Match(TokenKind.Identifier);
        var (colon, type) = ParseOptionalTypeAnnotation();
        var equals = Match(TokenKind.Equals);
        var initializer = ParseExpression();
        ConsumeStatementTerminator();
        return new LetStatementSyntax(keyword, identifier, colon, type, equals, initializer);
    }

    private VarStatementSyntax ParseVarStatement()
    {
        var keyword = Match(TokenKind.VarKeyword);
        var identifier = Match(TokenKind.Identifier);
        var (colon, type) = ParseOptionalTypeAnnotation();
        var equals = Match(TokenKind.Equals);
        var initializer = ParseExpression();
        ConsumeStatementTerminator();
        return new VarStatementSyntax(keyword, identifier, colon, type, equals, initializer);
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration(SyntaxToken? publicKeyword = null, SyntaxToken? ffiKeyword = null)
    {
        var keyword = Match(TokenKind.FnKeyword);
        var identifier = Match(TokenKind.Identifier);
        var genericParameters = ParseOptionalGenericParameters();
        var leftParenthesis = Match(TokenKind.LeftParen);
        var parameters = ParseParameters();
        var rightParenthesis = Match(TokenKind.RightParen);
        SyntaxToken? arrow = null;
        TypeSyntax? returnType = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            arrow = NextToken();
            returnType = ParseType();
        }

        var body = ParseBlock();
        return new FunctionDeclarationSyntax(
            publicKeyword,
            ffiKeyword,
            keyword,
            identifier,
            genericParameters,
            leftParenthesis,
            parameters,
            rightParenthesis,
            arrow,
            returnType,
            body.LeftBrace,
            body);
    }

    private ObjectDeclarationSyntax ParseObjectDeclaration(SyntaxToken? publicKeyword)
    {
        var keyword = NextToken();
        var kind = keyword.Kind switch
        {
            TokenKind.ClassKeyword => ObjectDeclarationKind.Class,
            TokenKind.StructKeyword => ObjectDeclarationKind.Struct,
            TokenKind.InterfaceKeyword => ObjectDeclarationKind.Interface,
            _ => throw new InvalidOperationException("Expected an object declaration keyword.")
        };
        var identifier = Match(TokenKind.Identifier);
        var genericParameters = ParseOptionalGenericParameters();
        SyntaxToken? implementsKeyword = null;
        var implementedInterfaces = new List<TypeSyntax>();
        if (Current.Kind == TokenKind.ImplementsKeyword)
        {
            implementsKeyword = NextToken();
            do
            {
                implementedInterfaces.Add(ParseType());
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                NextToken();
            }
            while (Current.Kind is not TokenKind.LeftBrace and not TokenKind.EndOfFile);
        }

        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var members = new List<ObjectMemberSyntax>();
        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            members.Add(kind == ObjectDeclarationKind.Interface ? ParseInterfaceMember() : ParseObjectMember());
        }

        var rightBrace = Match(TokenKind.RightBrace);
        return new ObjectDeclarationSyntax(
            publicKeyword,
            keyword,
            kind,
            identifier,
            genericParameters,
            implementsKeyword,
            implementedInterfaces,
            leftBrace,
            members,
            rightBrace);
    }

    private ObjectMemberSyntax ParseObjectMember()
    {
        if (Current.Kind == TokenKind.FnKeyword)
        {
            return new ObjectMethodSyntax(ParseFunctionDeclaration());
        }

        var varKeyword = Current.Kind == TokenKind.VarKeyword ? NextToken() : null;
        var identifier = Match(TokenKind.Identifier);
        var colon = Match(TokenKind.Colon);
        var type = ParseType();
        ConsumeStatementTerminator();
        return new ObjectFieldSyntax(varKeyword, identifier, colon, type);
    }

    private InterfaceMethodSyntax ParseInterfaceMember()
    {
        var fn = Match(TokenKind.FnKeyword);
        var identifier = Match(TokenKind.Identifier);
        var genericParameters = ParseOptionalGenericParameters();
        var left = Match(TokenKind.LeftParen);
        var parameters = ParseParameters();
        var right = Match(TokenKind.RightParen);
        SyntaxToken? arrow = null;
        TypeSyntax? returnType = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            arrow = NextToken();
            returnType = ParseType();
        }

        ConsumeStatementTerminator();
        return new InterfaceMethodSyntax(fn, identifier, genericParameters, left, parameters, right, arrow, returnType);
    }

    private RecordDeclarationSyntax ParseRecordDeclaration()
    {
        var keyword = Match(TokenKind.RecordKeyword);
        var identifier = Match(TokenKind.Identifier);
        var genericParameters = ParseOptionalGenericParameters();
        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var members = new List<RecordMemberSyntax>();

        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            members.Add(ParseRecordMember());
        }

        var rightBrace = Match(TokenKind.RightBrace);
        return new RecordDeclarationSyntax(keyword, identifier, genericParameters, leftBrace, members, rightBrace);
    }

    private RecordMemberSyntax ParseRecordMember()
    {
        if (Current.Kind == TokenKind.FnKeyword)
        {
            return new RecordMethodSyntax(ParseFunctionDeclaration());
        }

        if (Current.Kind != TokenKind.Identifier)
        {
            _diagnostics.ReportError(
                "P005",
                Current.Span,
                "Expected a record field or method declaration.",
                "Declare a field as 'name: Type' or begin a method with 'fn'.");
            SynchronizeToStatementBoundary();
            var missing = SyntaxToken.Missing(TokenKind.Identifier, Current.Span.Start);
            return new RecordFieldSyntax(missing, SyntaxToken.Missing(TokenKind.Colon, Current.Span.Start), new NamedTypeSyntax(missing, null, [], null, null));
        }

        var identifier = Match(TokenKind.Identifier);
        var colon = Match(TokenKind.Colon);
        var type = ParseType();
        ConsumeStatementTerminator();
        return new RecordFieldSyntax(identifier, colon, type);
    }

    private ReturnStatementSyntax ParseReturnStatement()
    {
        var keyword = Match(TokenKind.ReturnKeyword);
        ExpressionSyntax? expression = null;
        if (Current.Kind is not TokenKind.NewLine and not TokenKind.Semicolon and not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            expression = ParseExpression();
        }

        ConsumeStatementTerminator();
        return new ReturnStatementSyntax(keyword, expression);
    }

    private AssertStatementSyntax ParseAssertStatement()
    {
        var keyword = Match(TokenKind.AssertKeyword);
        var condition = ParseExpression();
        SyntaxToken? comma = null;
        ExpressionSyntax? message = null;
        if (Current.Kind == TokenKind.Comma)
        {
            comma = NextToken();
            message = ParseExpression();
        }

        ConsumeStatementTerminator();
        return new AssertStatementSyntax(keyword, condition, comma, message);
    }

    private IfStatementSyntax ParseIfStatement()
    {
        var keyword = Match(TokenKind.IfKeyword);
        var condition = ParseExpression();
        var thenBlock = ParseBlock();
        ElseClauseSyntax? elseClause = null;

        SkipNewLines();
        if (Current.Kind == TokenKind.ElseKeyword)
        {
            var elseKeyword = NextToken();
            var elseBlock = ParseBlock();
            elseClause = new ElseClauseSyntax(elseKeyword, elseBlock.LeftBrace, elseBlock);
        }

        return new IfStatementSyntax(keyword, condition, thenBlock.LeftBrace, thenBlock, elseClause);
    }

    private ForStatementSyntax ParseForStatement()
    {
        var keyword = Match(TokenKind.ForKeyword);
        var identifier = Match(TokenKind.Identifier);
        var inKeyword = Match(TokenKind.InKeyword);
        var collection = ParseExpression();
        var body = ParseBlock();
        return new ForStatementSyntax(keyword, identifier, inKeyword, collection, body.LeftBrace, body);
    }

    private ExpressionStatementSyntax ParseExpressionStatement()
    {
        var expression = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatementSyntax(expression);
    }

    private BlockSyntax ParseBlock()
    {
        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var statements = new List<StatementSyntax>();

        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            statements.Add(ParseStatement());
        }

        var rightBrace = Match(TokenKind.RightBrace);
        return new BlockSyntax(leftBrace, statements, rightBrace);
    }

    private List<GenericParameterSyntax> ParseOptionalGenericParameters()
    {
        if (Current.Kind != TokenKind.Less)
        {
            return [];
        }

        _ = Match(TokenKind.Less);
        var parameters = new List<GenericParameterSyntax>();
        if (Current.Kind != TokenKind.Greater)
        {
            do
            {
                var identifier = Match(TokenKind.Identifier);
                SyntaxToken? colon = null;
                TypeSyntax? constraint = null;
                if (Current.Kind == TokenKind.Colon)
                {
                    colon = NextToken();
                    constraint = ParseType();
                }

                parameters.Add(new GenericParameterSyntax(identifier, colon, constraint));
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                NextToken();
            }
            while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
        }

        _ = Match(TokenKind.Greater);
        return parameters;
    }

    private List<ParameterSyntax> ParseParameters()
    {
        var parameters = new List<ParameterSyntax>();
        if (Current.Kind == TokenKind.RightParen)
        {
            return parameters;
        }

        do
        {
            var identifier = Match(TokenKind.Identifier);
            var colon = Match(TokenKind.Colon);
            var type = ParseType();
            parameters.Add(new ParameterSyntax(identifier, colon, type));
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            NextToken();
        }
        while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);

        return parameters;
    }

    private (SyntaxToken? Colon, TypeSyntax? Type) ParseOptionalTypeAnnotation()
    {
        if (Current.Kind != TokenKind.Colon)
        {
            return (null, null);
        }

        var colon = NextToken();
        return (colon, ParseType());
    }

    private NamedTypeSyntax ParseType()
    {
        var identifier = Match(TokenKind.Identifier);
        SyntaxToken? less = null;
        SyntaxToken? greater = null;
        IReadOnlyList<TypeSyntax> typeArguments = [];

        if (Current.Kind == TokenKind.Less)
        {
            less = NextToken();
            var arguments = new List<TypeSyntax>();
            if (Current.Kind != TokenKind.Greater)
            {
                do
                {
                    arguments.Add(ParseType());
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    NextToken();
                }
                while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
            }

            greater = Match(TokenKind.Greater);
            typeArguments = arguments;
        }

        SyntaxToken? question = Current.Kind == TokenKind.Question ? NextToken() : null;
        return new NamedTypeSyntax(identifier, less, typeArguments, greater, question);
    }

    private ExpressionSyntax ParseExpression() => ParseAssignmentExpression();

    private ExpressionSyntax ParseAssignmentExpression()
    {
        var target = ParseBinaryExpression();
        if (Current.Kind != TokenKind.Equals)
        {
            return target;
        }

        var equals = NextToken();
        var value = ParseAssignmentExpression();
        if (target is not NameExpressionSyntax and not IndexExpressionSyntax and not MemberAccessExpressionSyntax)
        {
            _diagnostics.ReportError(
                "P006",
                target.Span,
                "The left side of an assignment must be a variable name, mutable field, or indexed collection.",
                "Assign to a variable declared with 'var', a mutable field, or an indexed collection element.");
        }

        return new AssignmentExpressionSyntax(target, equals, value);
    }

    private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        var unaryPrecedence = GetUnaryPrecedence(Current.Kind);
        if (unaryPrecedence != 0)
        {
            var operatorToken = NextToken();
            var operand = ParseBinaryExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(operatorToken, operand);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
            {
                break;
            }

            var operatorToken = NextToken();
            var right = ParseBinaryExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorToken, right);
        }

        return left;
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        var expression = ParsePrimaryExpression();
        while (Current.Kind == TokenKind.LeftParen || Current.Kind == TokenKind.LeftBracket || Current.Kind == TokenKind.Dot || CanParseGenericCall())
        {
            if (Current.Kind == TokenKind.Dot)
            {
                var dot = NextToken();
                var member = Match(TokenKind.Identifier);
                expression = new MemberAccessExpressionSyntax(expression, dot, member);
                continue;
            }

            if (Current.Kind == TokenKind.LeftBracket)
            {
                var leftBracket = NextToken();
                var index = ParseExpression();
                var rightBracket = Match(TokenKind.RightBracket);
                expression = new IndexExpressionSyntax(expression, leftBracket, index, rightBracket);
                continue;
            }

            SyntaxToken? less = null;
            SyntaxToken? greater = null;
            IReadOnlyList<TypeSyntax> typeArguments = [];
            if (Current.Kind == TokenKind.Less)
            {
                (less, typeArguments, greater) = ParseTypeArguments();
            }

            var leftParenthesis = Match(TokenKind.LeftParen);
            var arguments = new List<ExpressionSyntax>();
            if (Current.Kind != TokenKind.RightParen)
            {
                do
                {
                    arguments.Add(ParseExpression());
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    NextToken();
                }
                while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);
            }

            var rightParenthesis = Match(TokenKind.RightParen);
            expression = new CallExpressionSyntax(expression, less, typeArguments, greater, leftParenthesis, arguments, rightParenthesis);
        }

        return expression;
    }

    private (SyntaxToken Less, IReadOnlyList<TypeSyntax> Arguments, SyntaxToken Greater) ParseTypeArguments()
    {
        var less = Match(TokenKind.Less);
        var arguments = new List<TypeSyntax>();
        if (Current.Kind != TokenKind.Greater)
        {
            do
            {
                arguments.Add(ParseType());
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                NextToken();
            }
            while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
        }

        var greater = Match(TokenKind.Greater);
        return (less, arguments, greater);
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        if (Current.Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.StringLiteral or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NilKeyword)
        {
            return new LiteralExpressionSyntax(NextToken());
        }

        if (Current.Kind == TokenKind.Identifier)
        {
            return new NameExpressionSyntax(NextToken());
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            var left = NextToken();
            var expression = ParseExpression();
            var right = Match(TokenKind.RightParen);
            return new ParenthesizedExpressionSyntax(left, expression, right);
        }

        if (Current.Kind == TokenKind.LeftBracket)
        {
            var left = NextToken();
            var elements = new List<ExpressionSyntax>();
            if (Current.Kind != TokenKind.RightBracket)
            {
                do
                {
                    elements.Add(ParseExpression());
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    NextToken();
                }
                while (Current.Kind is not TokenKind.RightBracket and not TokenKind.EndOfFile);
            }

            var right = Match(TokenKind.RightBracket);
            return new ListExpressionSyntax(left, elements, right);
        }

        _diagnostics.ReportError(
            "P004",
            Current.Span,
            $"Expected an expression, found '{Current.Kind}'.",
            "Use a literal, variable name, list, call, or parenthesized expression.");
        var unexpected = Current.Kind == TokenKind.EndOfFile ? SyntaxToken.Missing(TokenKind.Identifier, Current.Span.Start) : NextToken();
        return new LiteralExpressionSyntax(unexpected);
    }

    private bool CanParseGenericCall()
    {
        if (Current.Kind != TokenKind.Less)
        {
            return false;
        }

        var depth = 0;
        for (var index = _position; index < _tokens.Count; index++)
        {
            var kind = _tokens[index].Kind;
            switch (kind)
            {
                case TokenKind.Less:
                    depth++;
                    break;
                case TokenKind.Greater:
                    depth--;
                    if (depth == 0)
                    {
                        return index + 1 < _tokens.Count && _tokens[index + 1].Kind == TokenKind.LeftParen;
                    }

                    break;
                case TokenKind.Identifier:
                case TokenKind.Comma:
                case TokenKind.Question:
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private void ConsumeStatementTerminator()
    {
        if (Current.Kind == TokenKind.Semicolon)
        {
            NextToken();
            SkipNewLines();
            return;
        }

        _diagnostics.ReportError(
            "P002",
            Current.Span,
            "Expected the end of the statement.",
            "Terminate this statement with ';'.");
        SynchronizeToStatementBoundary();
    }

    private void SynchronizeToStatementBoundary()
    {
        while (Current.Kind is not TokenKind.NewLine and not TokenKind.Semicolon and not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            NextToken();
        }

        if (Current.Kind == TokenKind.Semicolon)
        {
            NextToken();
        }

        SkipNewLines();
    }

    private void SkipNewLines()
    {
        while (Current.Kind == TokenKind.NewLine)
        {
            NextToken();
        }
    }

    private SyntaxToken Match(TokenKind expectedKind)
    {
        if (Current.Kind == expectedKind)
        {
            return NextToken();
        }

        _diagnostics.ReportError(
            "P001",
            Current.Span,
            $"Expected '{expectedKind}', found '{Current.Kind}'.",
            $"Insert '{GetDisplayText(expectedKind)}' here.");
        return SyntaxToken.Missing(expectedKind, Current.Span.Start);
    }

    private SyntaxToken NextToken()
    {
        var current = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return current;
    }

    private SyntaxToken Current => _tokens[_position];

    private static int GetUnaryPrecedence(TokenKind kind) => kind == TokenKind.Minus ? 6 : 0;

    private static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.EqualsEquals or TokenKind.BangEquals => 1,
        TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater or TokenKind.GreaterOrEqual => 2,
        TokenKind.Plus or TokenKind.Minus => 3,
        TokenKind.Star or TokenKind.Slash => 4,
        _ => 0
    };

    private static string GetDisplayText(TokenKind kind) => kind switch
    {
        TokenKind.NewLine => "a new line",
        TokenKind.Semicolon => "';'",
        TokenKind.LeftBrace => "'{'",
        TokenKind.RightBrace => "'}'",
        TokenKind.Identifier => "an identifier",
        TokenKind.EndOfFile => "the end of the file",
        _ => kind.ToString()
    };
}
