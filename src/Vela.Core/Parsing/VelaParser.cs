using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Core.Parsing;

/// <summary>Builds a syntax tree from Vela tokens while recovering from independent errors.</summary>
public sealed class VelaParser
{
    private readonly SourceText _source;
    private readonly IReadOnlyList<SyntaxToken> _tokens;
    private readonly IReadOnlyList<SyntaxTrivia> _trivia;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly HashSet<int> _usedDocumentation = [];
    private int _position;

    public VelaParser(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        var lexResult = new VelaLexer(source).Lex();
        _tokens = lexResult.Tokens;
        _trivia = lexResult.Trivia;
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

            var documentation = FindDocumentation(Current.Span.Start);
            members.Add(ParseStatement(documentation));
        }

        var endOfFile = Match(TokenKind.EndOfFile);
        ReportOrphanDocumentation();
        return new ParseResult(new CompilationUnitSyntax(members, endOfFile), _diagnostics.Items.ToArray());
    }

    private List<AttributeSyntax> ParseAttributes()
    {
        var attributes = new List<AttributeSyntax>();
        while (Current.Kind == TokenKind.At)
        {
            var at = NextToken();
            var name = Match(TokenKind.Identifier);
            SyntaxToken? left = null;
            SyntaxToken? right = null;
            var arguments = new List<ExpressionSyntax>();
            if (Current.Kind == TokenKind.LeftParen)
            {
                left = NextToken();
                SkipNewLines();
                if (Current.Kind != TokenKind.RightParen)
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                        if (Current.Kind != TokenKind.Comma)
                        {
                            break;
                        }

                        _ = NextToken();
                        SkipNewLines();
                    }
                    while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);
                }

                SkipNewLines();
                right = Match(TokenKind.RightParen);
            }

            attributes.Add(new AttributeSyntax(at, name, left, arguments, right));
            SkipNewLines();
        }

        return attributes;
    }

    private StatementSyntax ParseStatement(
        DocumentationCommentSyntax? documentation = null,
        IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        attributes ??= Current.Kind == TokenKind.At ? ParseAttributes() : [];
        var statement = Current.Kind switch
        {
            TokenKind.IncludeKeyword => ParseIncludeDirective(),
            TokenKind.PublicKeyword => ParsePublicDeclaration(documentation, attributes),
            TokenKind.LetKeyword => ParseLetStatement(),
            TokenKind.VarKeyword => ParseVarStatement(),
            TokenKind.AsyncKeyword => ParseAsyncFunctionDeclaration(documentation: documentation, attributes: attributes),
            TokenKind.FnKeyword => ParseFunctionDeclaration(documentation: documentation, attributes: attributes),
            TokenKind.RecordKeyword => ParseRecordDeclaration(documentation, attributes),
            TokenKind.EnumKeyword => ParseEnumDeclaration(null, documentation, attributes),
            TokenKind.ClassKeyword or TokenKind.StructKeyword or TokenKind.InterfaceKeyword => ParseObjectDeclaration(null, documentation, attributes),
            TokenKind.ReturnKeyword => ParseReturnStatement(),
            TokenKind.AssertKeyword => ParseAssertStatement(),
            TokenKind.DeferKeyword => ParseDeferStatement(),
            TokenKind.TryKeyword => ParseTryStatement(),
            TokenKind.IfKeyword => ParseIfStatement(),
            TokenKind.ForKeyword => ParseForStatement(),
            TokenKind.WhileKeyword => ParseWhileStatement(),
            TokenKind.BreakKeyword => ParseBreakStatement(),
            TokenKind.ContinueKeyword => ParseContinueStatement(),
            TokenKind.SwitchKeyword => ParseSwitchStatement(),
            TokenKind.MatchKeyword => ParseMatchStatement(),
            _ => ParseExpressionStatement()
        };

        if (attributes.Count > 0 && statement is not FunctionDeclarationSyntax and not RecordDeclarationSyntax and not ObjectDeclarationSyntax and not EnumDeclarationSyntax)
        {
            _diagnostics.ReportError("P012", attributes[0].Span, "Attributes can only target declarations or object members.", "Move the attribute directly before a function, type, field, or method declaration.");
        }

        return statement;
    }

    private StatementSyntax ParsePublicDeclaration(DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax> attributes)
    {
        var publicKeyword = Match(TokenKind.PublicKeyword);
        if (Current.Kind is TokenKind.FfiKeyword or TokenKind.AsyncKeyword or TokenKind.FnKeyword)
        {
            var ffiKeyword = Current.Kind == TokenKind.FfiKeyword ? NextToken() : null;
            var asyncKeyword = Current.Kind == TokenKind.AsyncKeyword ? NextToken() : null;
            return ParseFunctionDeclaration(publicKeyword, ffiKeyword, asyncKeyword, documentation, attributes);
        }

        if (Current.Kind is TokenKind.ClassKeyword or TokenKind.StructKeyword or TokenKind.InterfaceKeyword)
        {
            return ParseObjectDeclaration(publicKeyword, documentation, attributes);
        }

        if (Current.Kind == TokenKind.EnumKeyword)
        {
            return ParseEnumDeclaration(publicKeyword, documentation, attributes);
        }

        _diagnostics.ReportError(
            "P007",
            Current.Span,
            "The 'public' modifier must declare a function, class, struct, interface, or enum.",
            "Use 'public fn', 'public async fn', 'public ffi fn', 'public class', 'public struct', 'public interface', or 'public enum'.");
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

    private StatementSyntax ParseLetStatement()
    {
        var keyword = Match(TokenKind.LetKeyword);
        if (Current.Kind == TokenKind.LeftParen)
        {
            var left = NextToken();
            var bindings = ParseDestructuringNames(TokenKind.RightParen);
            var right = Match(TokenKind.RightParen);
            var tupleEquals = Match(TokenKind.Equals);
            var tupleInitializer = ParseExpression();
            ConsumeStatementTerminator();
            return new TupleDestructuringStatementSyntax(keyword, left, bindings, right, tupleEquals, tupleInitializer);
        }

        if (Current.Kind == TokenKind.Identifier && PeekToken(1).Kind == TokenKind.LeftBrace)
        {
            var recordType = NextToken();
            var left = NextToken();
            var fields = ParseDestructuringNames(TokenKind.RightBrace);
            var right = Match(TokenKind.RightBrace);
            var recordEquals = Match(TokenKind.Equals);
            var recordInitializer = ParseExpression();
            ConsumeStatementTerminator();
            return new RecordDestructuringStatementSyntax(keyword, recordType, left, fields, right, recordEquals, recordInitializer);
        }

        var identifier = Match(TokenKind.Identifier);
        var (colon, type) = ParseOptionalTypeAnnotation();
        var equals = Match(TokenKind.Equals);
        var initializer = ParseExpression();
        ConsumeStatementTerminator();
        return new LetStatementSyntax(keyword, identifier, colon, type, equals, initializer);
    }

    private List<SyntaxToken> ParseDestructuringNames(TokenKind closingKind)
    {
        var names = new List<SyntaxToken>();
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        if (Current.Kind == closingKind)
        {
            _diagnostics.ReportError("P016", Current.Span, "A destructuring pattern cannot be empty.", "Bind at least one name or use a normal let declaration.");
            return names;
        }

        do
        {
            var name = Match(TokenKind.Identifier);
            if (name.Text != "_" && !uniqueNames.Add(name.Text))
            {
                _diagnostics.ReportError("P016", name.Span, $"Duplicate destructuring binding '{name.Text}'.", "Use each binding name once or use '_' to discard a value.");
            }

            names.Add(name);
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            _ = NextToken();
        }
        while (Current.Kind != closingKind && Current.Kind != TokenKind.EndOfFile);

        return names;
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

    private FunctionDeclarationSyntax ParseFunctionDeclaration(
        SyntaxToken? publicKeyword = null,
        SyntaxToken? ffiKeyword = null,
        SyntaxToken? asyncKeyword = null,
        DocumentationCommentSyntax? documentation = null,
        IReadOnlyList<AttributeSyntax>? attributes = null)
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
        var function = new FunctionDeclarationSyntax(
            publicKeyword,
            ffiKeyword,
            asyncKeyword,
            keyword,
            identifier,
            genericParameters,
            leftParenthesis,
            parameters,
            rightParenthesis,
            arrow,
            returnType,
            body.LeftBrace,
            body,
            documentation,
            attributes ?? []);
        UseDocumentation(documentation);
        ValidateFunctionDocumentation(function);
        return function;
    }

    private FunctionDeclarationSyntax ParseAsyncFunctionDeclaration(
        SyntaxToken? publicKeyword = null,
        SyntaxToken? ffiKeyword = null,
        DocumentationCommentSyntax? documentation = null,
        IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        var asyncKeyword = Match(TokenKind.AsyncKeyword);
        if (Current.Kind != TokenKind.FnKeyword)
        {
            _diagnostics.ReportError("P011", Current.Span, "The 'async' modifier must precede a function declaration.", "Use syntax such as 'async fn fetch() -> Text { ... }'.");
        }

        return ParseFunctionDeclaration(publicKeyword, ffiKeyword, asyncKeyword, documentation, attributes);
    }

    private ObjectDeclarationSyntax ParseObjectDeclaration(
        SyntaxToken? publicKeyword,
        DocumentationCommentSyntax? documentation,
        IReadOnlyList<AttributeSyntax>? attributes = null)
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
        SyntaxToken? constructorLeftParenthesis = null;
        SyntaxToken? constructorRightParenthesis = null;
        IReadOnlyList<ParameterSyntax> constructorParameters = [];
        if (kind == ObjectDeclarationKind.Class)
        {
            if (Current.Kind != TokenKind.LeftParen)
            {
                _diagnostics.ReportError("P013", Current.Span, $"Class '{identifier.Text}' must declare a primary constructor parameter list.", $"Write 'class {identifier.Text}()' even when the constructor has no parameters.");
                constructorLeftParenthesis = SyntaxToken.Missing(TokenKind.LeftParen, Current.Span.Start);
                constructorRightParenthesis = SyntaxToken.Missing(TokenKind.RightParen, Current.Span.Start);
            }
            else
            {
                constructorLeftParenthesis = NextToken();
                constructorParameters = ParseParameters();
                constructorRightParenthesis = Match(TokenKind.RightParen);
            }
        }
        else if (Current.Kind == TokenKind.LeftParen)
        {
            _diagnostics.ReportError("P013", Current.Span, $"Only classes can declare a primary constructor; '{identifier.Text}' is a {kind.ToString().ToLowerInvariant()}.", "Remove the parameter list and keep struct construction field-based.");
            _ = NextToken();
            _ = ParseParameters();
            _ = Match(TokenKind.RightParen);
        }

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

            var memberDocumentation = FindDocumentation(Current.Span.Start);
            var memberAttributes = Current.Kind == TokenKind.At ? ParseAttributes() : [];
            members.Add(kind == ObjectDeclarationKind.Interface
                ? ParseInterfaceMember(memberDocumentation, memberAttributes)
                : ParseObjectMember(memberDocumentation, memberAttributes));
        }

        var rightBrace = Match(TokenKind.RightBrace);
        var declaration = new ObjectDeclarationSyntax(
            publicKeyword,
            keyword,
            kind,
            identifier,
            genericParameters,
            constructorLeftParenthesis,
            constructorParameters,
            constructorRightParenthesis,
            implementsKeyword,
            implementedInterfaces,
            leftBrace,
            members,
            rightBrace,
            documentation,
            attributes ?? []);
        UseDocumentation(documentation);
        return declaration;
    }

    private ObjectMemberSyntax ParseObjectMember(DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        if (Current.Kind is TokenKind.FnKeyword or TokenKind.AsyncKeyword)
        {
            return new ObjectMethodSyntax(Current.Kind == TokenKind.AsyncKeyword
                ? ParseAsyncFunctionDeclaration(documentation: documentation, attributes: attributes)
                : ParseFunctionDeclaration(documentation: documentation, attributes: attributes));
        }

        var varKeyword = Current.Kind == TokenKind.VarKeyword ? NextToken() : null;
        var identifier = Match(TokenKind.Identifier);
        var colon = Match(TokenKind.Colon);
        var type = ParseType();
        SyntaxToken? equals = null;
        ExpressionSyntax? initializer = null;
        if (Current.Kind == TokenKind.Equals)
        {
            equals = NextToken();
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();
        UseDocumentation(documentation);
        return new ObjectFieldSyntax(varKeyword, identifier, colon, type, equals, initializer, documentation, attributes ?? []);
    }

    private InterfaceMethodSyntax ParseInterfaceMember(DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        var asyncKeyword = Current.Kind == TokenKind.AsyncKeyword ? NextToken() : null;
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
        var method = new InterfaceMethodSyntax(asyncKeyword, fn, identifier, genericParameters, left, parameters, right, arrow, returnType, documentation, attributes ?? []);
        UseDocumentation(documentation);
        ValidateDocumentationParameters(documentation, parameters, returnType);
        return method;
    }

    private RecordDeclarationSyntax ParseRecordDeclaration(DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax>? attributes = null)
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

            var memberDocumentation = FindDocumentation(Current.Span.Start);
            var memberAttributes = Current.Kind == TokenKind.At ? ParseAttributes() : [];
            members.Add(ParseRecordMember(memberDocumentation, memberAttributes));
        }

        var rightBrace = Match(TokenKind.RightBrace);
        UseDocumentation(documentation);
        return new RecordDeclarationSyntax(keyword, identifier, genericParameters, leftBrace, members, rightBrace, documentation, attributes ?? []);
    }

    private RecordMemberSyntax ParseRecordMember(DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        if (Current.Kind == TokenKind.FnKeyword)
        {
            return new RecordMethodSyntax(ParseFunctionDeclaration(documentation: documentation, attributes: attributes));
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
            return new RecordFieldSyntax(missing, SyntaxToken.Missing(TokenKind.Colon, Current.Span.Start), new NamedTypeSyntax(missing, null, [], null, null), documentation, attributes ?? []);
        }

        var identifier = Match(TokenKind.Identifier);
        var colon = Match(TokenKind.Colon);
        var type = ParseType();
        ConsumeStatementTerminator();
        UseDocumentation(documentation);
        return new RecordFieldSyntax(identifier, colon, type, documentation, attributes ?? []);
    }

    private EnumDeclarationSyntax ParseEnumDeclaration(SyntaxToken? publicKeyword, DocumentationCommentSyntax? documentation, IReadOnlyList<AttributeSyntax>? attributes = null)
    {
        var enumKeyword = Match(TokenKind.EnumKeyword);
        var identifier = Match(TokenKind.Identifier);
        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var members = new List<EnumMemberSyntax>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            var memberDocumentation = FindDocumentation(Current.Span.Start);
            var memberAttributes = Current.Kind == TokenKind.At ? ParseAttributes() : [];
            var member = Match(TokenKind.Identifier);
            if (!names.Add(member.Text))
            {
                _diagnostics.ReportError("P009", member.Span, $"Duplicate enum member '{member.Text}'.", "Use a unique name for each enum member.");
            }

            SyntaxToken? separator = null;
            if (Current.Kind is TokenKind.Comma or TokenKind.Semicolon)
            {
                separator = NextToken();
                SkipNewLines();
            }
            else if (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile and not TokenKind.NewLine)
            {
                _diagnostics.ReportError("P009", Current.Span, "Expected ',' or ';' after an enum member.", "Separate enum members with ',' or ';'.");
                SynchronizeToStatementBoundary();
            }

            UseDocumentation(memberDocumentation);
            members.Add(new EnumMemberSyntax(member, separator, memberDocumentation, memberAttributes));
        }

        var rightBrace = Match(TokenKind.RightBrace);
        if (members.Count == 0)
        {
            _diagnostics.ReportError("P009", identifier.Span, "An enum must declare at least one member.", "Add one or more named enum members.");
        }

        UseDocumentation(documentation);
        return new EnumDeclarationSyntax(publicKeyword, enumKeyword, identifier, leftBrace, members, rightBrace, documentation, attributes ?? []);
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

    private DeferStatementSyntax ParseDeferStatement()
    {
        var keyword = Match(TokenKind.DeferKeyword);
        var invocation = ParseExpression();
        if (invocation is not CallExpressionSyntax)
        {
            _diagnostics.ReportError("P010", invocation.Span, "A defer statement requires a call expression.", "Use syntax such as 'defer tcp.close(connection);'.");
        }

        ConsumeStatementTerminator();
        return new DeferStatementSyntax(keyword, invocation);
    }

    private TryStatementSyntax ParseTryStatement()
    {
        var tryKeyword = Match(TokenKind.TryKeyword);
        var tryBlock = ParseBlock();
        SkipNewLines();
        var catches = new List<CatchClauseSyntax>();
        while (Current.Kind == TokenKind.CatchKeyword)
        {
            var catchKeyword = NextToken();
            var exceptionType = ParseType();
            var identifier = Match(TokenKind.Identifier);
            var block = ParseBlock();
            catches.Add(new CatchClauseSyntax(catchKeyword, exceptionType, identifier, block));
            SkipNewLines();
        }

        FinallyClauseSyntax? finallyClause = null;
        if (Current.Kind == TokenKind.FinallyKeyword)
        {
            var finallyKeyword = NextToken();
            finallyClause = new FinallyClauseSyntax(finallyKeyword, ParseBlock());
        }

        if (catches.Count == 0 && finallyClause is null)
        {
            _diagnostics.ReportError("P010", tryKeyword.Span, "A try statement requires at least one catch or finally block.", "Add 'catch VelaRuntimeException error { ... }' or 'finally { ... }'.");
        }

        return new TryStatementSyntax(tryKeyword, tryBlock, catches, finallyClause);
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

    private WhileStatementSyntax ParseWhileStatement()
    {
        var keyword = Match(TokenKind.WhileKeyword);
        var condition = ParseExpression();
        var body = ParseBlock();
        return new WhileStatementSyntax(keyword, condition, body.LeftBrace, body);
    }

    private BreakStatementSyntax ParseBreakStatement()
    {
        var keyword = Match(TokenKind.BreakKeyword);
        ConsumeStatementTerminator();
        return new BreakStatementSyntax(keyword);
    }

    private ContinueStatementSyntax ParseContinueStatement()
    {
        var keyword = Match(TokenKind.ContinueKeyword);
        ConsumeStatementTerminator();
        return new ContinueStatementSyntax(keyword);
    }

    private SwitchStatementSyntax ParseSwitchStatement()
    {
        var keyword = Match(TokenKind.SwitchKeyword);
        var expression = ParseExpression();
        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var cases = new List<SwitchCaseSyntax>();
        SwitchDefaultClauseSyntax? defaultClause = null;
        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            if (Current.Kind == TokenKind.CaseKeyword)
            {
                var caseKeyword = NextToken();
                var value = ParseExpression();
                var body = ParseBlock();
                cases.Add(new SwitchCaseSyntax(caseKeyword, value, body));
                continue;
            }

            if (Current.Kind == TokenKind.DefaultKeyword)
            {
                var defaultKeyword = NextToken();
                if (defaultClause is not null)
                {
                    _diagnostics.ReportError("P008", defaultKeyword.Span, "A switch statement can contain only one 'default' clause.", "Remove the duplicate default block.");
                }

                var body = ParseBlock();
                defaultClause ??= new SwitchDefaultClauseSyntax(defaultKeyword, body);
                continue;
            }

            _diagnostics.ReportError("P008", Current.Span, "Expected 'case', 'default', or '}' in switch statement.", "Add a case block or close the switch statement.");
            SynchronizeToStatementBoundary();
        }

        var rightBrace = Match(TokenKind.RightBrace);
        return new SwitchStatementSyntax(keyword, expression, leftBrace, cases, defaultClause, rightBrace);
    }

    private MatchStatementSyntax ParseMatchStatement()
    {
        var keyword = Match(TokenKind.MatchKeyword);
        var expression = ParseExpression();
        var leftBrace = Match(TokenKind.LeftBrace);
        SkipNewLines();
        var cases = new List<MatchCaseSyntax>();
        SwitchDefaultClauseSyntax? defaultClause = null;
        var sawDefault = false;
        while (Current.Kind is not TokenKind.RightBrace and not TokenKind.EndOfFile)
        {
            SkipNewLines();
            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                break;
            }

            if (Current.Kind == TokenKind.CaseKeyword)
            {
                var caseKeyword = NextToken();
                if (sawDefault)
                {
                    _diagnostics.ReportError("P015", caseKeyword.Span, "A match case cannot appear after the default block.", "Move the default block to the end of the match statement.");
                }

                var pattern = ParseMatchPattern();
                cases.Add(new MatchCaseSyntax(caseKeyword, pattern, ParseBlock()));
                continue;
            }

            if (Current.Kind == TokenKind.DefaultKeyword)
            {
                var defaultKeyword = NextToken();
                if (defaultClause is not null)
                {
                    _diagnostics.ReportError("P015", defaultKeyword.Span, "A match statement can contain only one default block.", "Remove the duplicate default block.");
                }

                var body = ParseBlock();
                defaultClause ??= new SwitchDefaultClauseSyntax(defaultKeyword, body);
                sawDefault = true;
                continue;
            }

            _diagnostics.ReportError("P015", Current.Span, "Expected 'case', 'default', or '}' in match statement.", "Add a pattern case or close the match statement.");
            SynchronizeToStatementBoundary();
        }

        return new MatchStatementSyntax(keyword, expression, leftBrace, cases, defaultClause, Match(TokenKind.RightBrace));
    }

    private MatchPatternSyntax ParseMatchPattern()
    {
        if (Current.Kind == TokenKind.Identifier
            && (PeekToken(1).Kind == TokenKind.LeftParen || Current.Text is "Some" or "None" or "Ok" or "Err"))
        {
            var variant = NextToken();
            SyntaxToken? left = null;
            SyntaxToken? binding = null;
            SyntaxToken? right = null;
            if (Current.Kind == TokenKind.LeftParen)
            {
                left = NextToken();
                if (Current.Kind != TokenKind.RightParen)
                {
                    binding = Match(TokenKind.Identifier);
                }

                right = Match(TokenKind.RightParen);
            }

            return new VariantMatchPatternSyntax(variant, left, binding, right);
        }

        return new ValueMatchPatternSyntax(ParseExpression());
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

            statements.Add(ParseStatement(FindDocumentation(Current.Span.Start)));
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
        SkipNewLines();
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
                SkipNewLines();
            }
            while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
        }

        _ = Match(TokenKind.Greater);
        return parameters;
    }

    private List<ParameterSyntax> ParseParameters()
    {
        var parameters = new List<ParameterSyntax>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sawOptional = false;
        SkipNewLines();
        if (Current.Kind == TokenKind.RightParen)
        {
            return parameters;
        }

        do
        {
            var identifier = Match(TokenKind.Identifier);
            var colon = Match(TokenKind.Colon);
            var type = ParseType();
            SyntaxToken? equals = null;
            ExpressionSyntax? defaultValue = null;
            if (Current.Kind == TokenKind.Equals)
            {
                equals = NextToken();
                defaultValue = ParseExpression();
                sawOptional = true;
            }
            else if (sawOptional)
            {
                _diagnostics.ReportError("P014", identifier.Span, $"Required parameter '{identifier.Text}' cannot follow an optional parameter.", "Move required parameters before parameters with default values.");
            }

            if (!names.Add(identifier.Text))
            {
                _diagnostics.ReportError("P014", identifier.Span, $"Duplicate parameter '{identifier.Text}'.", "Use a unique name for every parameter.");
            }

            parameters.Add(new ParameterSyntax(identifier, colon, type, equals, defaultValue));
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            NextToken();
            SkipNewLines();
        }
        while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);

        SkipNewLines();
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

    private TypeSyntax ParseType()
    {
        if (Current.Kind == TokenKind.LeftParen)
        {
            var left = NextToken();
            var elements = new List<TypeSyntax>();
            SkipNewLines();
            do
            {
                elements.Add(ParseType());
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                _ = NextToken();
                SkipNewLines();
            }
            while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);

            SkipNewLines();
            var right = Match(TokenKind.RightParen);
            if (elements.Count is < 2 or > 8)
            {
                _diagnostics.ReportError("P016", TextSpan.FromBounds(left.Span.Start, right.Span.End), "Tuple types require between two and eight elements.", "Use '(First, Second)' through an eight-element tuple.");
            }

            return new TupleTypeSyntax(left, elements, right);
        }

        if (Current.Kind == TokenKind.Identifier && string.Equals(Current.Text, "Fn", StringComparison.Ordinal))
        {
            return ParseFunctionType();
        }

        var identifier = Match(TokenKind.Identifier);
        if (string.Equals(identifier.Text, "Unit", StringComparison.Ordinal))
        {
            _diagnostics.ReportWarning("VELW001", identifier.Span, "Type 'Unit' is deprecated in user source.", "Use 'Void' for a function or method that returns no value.");
        }

        SyntaxToken? less = null;
        SyntaxToken? greater = null;
        IReadOnlyList<TypeSyntax> typeArguments = [];

        if (Current.Kind == TokenKind.Less)
        {
            less = NextToken();
            var arguments = new List<TypeSyntax>();
            SkipNewLines();
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
                    SkipNewLines();
                }
                while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
            }

            greater = Match(TokenKind.Greater);
            typeArguments = arguments;
        }

        SyntaxToken? question = Current.Kind == TokenKind.Question ? NextToken() : null;
        return new NamedTypeSyntax(identifier, less, typeArguments, greater, question);
    }

    private FunctionTypeSyntax ParseFunctionType()
    {
        var fn = Match(TokenKind.Identifier);
        var less = Match(TokenKind.Less);
        SkipNewLines();
        var leftParen = Match(TokenKind.LeftParen);
        var parameterTypes = new List<TypeSyntax>();
        SkipNewLines();
        if (Current.Kind != TokenKind.RightParen)
        {
            do
            {
                parameterTypes.Add(ParseType());
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                _ = NextToken();
                SkipNewLines();
            }
            while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);
        }

        SkipNewLines();
        var rightParen = Match(TokenKind.RightParen);
        SkipNewLines();
        var comma = Match(TokenKind.Comma);
        SkipNewLines();
        var returnType = ParseType();
        SkipNewLines();
        var greater = Match(TokenKind.Greater);
        return new FunctionTypeSyntax(fn, less, leftParen, parameterTypes, rightParen, comma, returnType, greater);
    }

    private LambdaExpressionSyntax ParseLambdaExpression()
    {
        var fn = Match(TokenKind.FnKeyword);
        var leftParenthesis = Match(TokenKind.LeftParen);
        var parameters = ParseParameters();
        var rightParenthesis = Match(TokenKind.RightParen);
        var arrow = Match(TokenKind.Arrow);
        var returnType = ParseType();
        var body = ParseBlock();
        return new LambdaExpressionSyntax(fn, leftParenthesis, parameters, rightParenthesis, arrow, returnType, body);
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
        if (Current.Kind == TokenKind.AwaitKeyword)
        {
            var awaitKeyword = NextToken();
            left = new AwaitExpressionSyntax(awaitKeyword, ParseBinaryExpression(7));
        }
        else if (unaryPrecedence != 0)
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
            var arguments = new List<CallArgumentSyntax>();
            var namedArguments = new HashSet<string>(StringComparer.Ordinal);
            var sawNamedArgument = false;
            SkipNewLines();
            if (Current.Kind != TokenKind.RightParen)
            {
                do
                {
                    SyntaxToken? argumentName = null;
                    SyntaxToken? colon = null;
                    if (Current.Kind == TokenKind.Identifier && PeekToken(1).Kind == TokenKind.Colon)
                    {
                        argumentName = NextToken();
                        colon = NextToken();
                        sawNamedArgument = true;
                        if (!namedArguments.Add(argumentName.Text))
                        {
                            _diagnostics.ReportError("P014", argumentName.Span, $"Named argument '{argumentName.Text}' is supplied more than once.", "Pass each named argument only once.");
                        }
                    }
                    else if (sawNamedArgument)
                    {
                        _diagnostics.ReportError("P014", Current.Span, "A positional argument cannot follow a named argument.", "Move all positional arguments before named arguments.");
                    }

                    arguments.Add(new CallArgumentSyntax(argumentName, colon, ParseExpression()));
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    NextToken();
                    SkipNewLines();
                }
                while (Current.Kind is not TokenKind.RightParen and not TokenKind.EndOfFile);
            }

            SkipNewLines();
            var rightParenthesis = Match(TokenKind.RightParen);
            expression = new CallExpressionSyntax(expression, less, typeArguments, greater, leftParenthesis, arguments, rightParenthesis);
        }

        return expression;
    }

    private (SyntaxToken Less, IReadOnlyList<TypeSyntax> Arguments, SyntaxToken Greater) ParseTypeArguments()
    {
        var less = Match(TokenKind.Less);
        var arguments = new List<TypeSyntax>();
        SkipNewLines();
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
                SkipNewLines();
            }
            while (Current.Kind is not TokenKind.Greater and not TokenKind.EndOfFile);
        }

        SkipNewLines();
        var greater = Match(TokenKind.Greater);
        return (less, arguments, greater);
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        if (Current.Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.StringLiteral or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NilKeyword)
        {
            return new LiteralExpressionSyntax(NextToken());
        }

        if (Current.Kind == TokenKind.FnKeyword)
        {
            return ParseLambdaExpression();
        }

        if (Current.Kind == TokenKind.Identifier)
        {
            return new NameExpressionSyntax(NextToken());
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            var left = NextToken();
            SkipNewLines();
            var first = ParseExpression();
            if (Current.Kind != TokenKind.Comma)
            {
                SkipNewLines();
                var right = Match(TokenKind.RightParen);
                return new ParenthesizedExpressionSyntax(left, first, right);
            }

            var elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                _ = NextToken();
                SkipNewLines();
                elements.Add(ParseExpression());
            }

            SkipNewLines();
            var tupleRight = Match(TokenKind.RightParen);
            if (elements.Count > 8)
            {
                _diagnostics.ReportError("P016", TextSpan.FromBounds(left.Span.Start, tupleRight.Span.End), "Tuple expressions support at most eight elements.", "Use a record for larger structured values.");
            }

            return new TupleExpressionSyntax(left, elements, tupleRight);
        }

        if (Current.Kind == TokenKind.LeftBracket)
        {
            var left = NextToken();
            var elements = new List<ExpressionSyntax>();
            SkipNewLines();
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
                    SkipNewLines();
                }
                while (Current.Kind is not TokenKind.RightBracket and not TokenKind.EndOfFile);
            }

            SkipNewLines();
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

    private DocumentationCommentSyntax? FindDocumentation(int declarationStart)
    {
        var candidates = _trivia
            .Where(trivia => trivia.IsDocumentation && trivia.Span.End <= declarationStart)
            .OrderBy(trivia => trivia.Span.Start)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var last = candidates[^1];
        if (!ContainsOnlyWhitespace(last.Span.End, declarationStart))
        {
            return null;
        }

        var comments = new List<SyntaxTrivia> { last };
        var groupStart = last.Span.Start;
        for (var index = candidates.Length - 2; index >= 0; index--)
        {
            var candidate = candidates[index];
            if (!ContainsOnlyWhitespace(candidate.Span.End, groupStart))
            {
                break;
            }

            comments.Add(candidate);
            groupStart = candidate.Span.Start;
        }

        comments.Reverse();
        return new DocumentationCommentSyntax(comments);
    }

    private bool ContainsOnlyWhitespace(int start, int end) => _source.Text[start..end].All(char.IsWhiteSpace);

    private void UseDocumentation(DocumentationCommentSyntax? documentation)
    {
        if (documentation is null)
        {
            return;
        }

        foreach (var comment in documentation.Comments)
        {
            _usedDocumentation.Add(comment.Span.Start);
        }
    }

    private void ReportOrphanDocumentation()
    {
        foreach (var comment in _trivia.Where(trivia => trivia.IsDocumentation && !_usedDocumentation.Contains(trivia.Span.Start)))
        {
            _diagnostics.ReportWarning(
                "DOC001",
                comment.Span,
                "Documentation comment does not document a declaration.",
                "Place it directly before a function, type, member, record, or enum declaration.");
        }
    }

    private void ValidateFunctionDocumentation(FunctionDeclarationSyntax function) =>
        ValidateDocumentationParameters(function.Documentation, function.Parameters, function.ReturnType);

    private void ValidateDocumentationParameters(
        DocumentationCommentSyntax? documentation,
        IReadOnlyList<ParameterSyntax> parameters,
        TypeSyntax? returnType)
    {
        if (documentation is null)
        {
            return;
        }

        foreach (var line in documentation.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("@param", StringComparison.Ordinal))
            {
                var parameterName = line["@param".Length..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(parameterName) || !parameters.Any(parameter => parameter.Identifier.Text == parameterName))
                {
                    _diagnostics.ReportWarning("DOC002", documentation.Span, $"Documentation parameter '{parameterName ?? string.Empty}' does not match a declared parameter.", "Use a parameter name declared by this function or method.");
                }

                continue;
            }

            if (line.StartsWith("@returns", StringComparison.Ordinal)
                && (returnType is null || returnType is NamedTypeSyntax { Identifier.Text: "Unit" or "Void" }))
            {
                _diagnostics.ReportWarning("DOC003", documentation.Span, "A Void declaration should not document a return value.", "Remove '@returns' or add a non-Void return type.");
            }
        }
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

    private SyntaxToken PeekToken(int offset)
    {
        var index = Math.Min(_position + offset, _tokens.Count - 1);
        return _tokens[index];
    }

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
