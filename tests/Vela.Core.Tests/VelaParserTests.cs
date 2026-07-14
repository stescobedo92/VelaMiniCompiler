using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Parsing;
using Vela.Core.Source;
using Vela.Core.Syntax;
using Xunit;

namespace Vela.Core.Tests;

public sealed class VelaParserTests
{
    [Fact]
    public void ParseGenericFunctionAndCallCapturesGenericSyntax()
    {
        const string code = """
            record Box<T> {
                value: T;
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            var result: Box<Int> = Box<Int>(identity<Int>(1));
            """;

        var result = VelaParser.Parse(new SourceText(code, "generic.vela"));

        Assert.Empty(result.Diagnostics);
        var record = Assert.IsType<RecordDeclarationSyntax>(result.Root.Members[0]);
        Assert.Equal("T", Assert.Single(record.GenericParameters).Identifier.Text);
        var field = Assert.IsType<RecordFieldSyntax>(Assert.Single(record.Members));
        Assert.Equal("T", Assert.IsType<NamedTypeSyntax>(field.Type).Identifier.Text);

        var function = Assert.IsType<FunctionDeclarationSyntax>(result.Root.Members[1]);
        Assert.Equal("T", Assert.Single(function.GenericParameters).Identifier.Text);
        Assert.Equal("T", Assert.IsType<NamedTypeSyntax>(function.ReturnType).Identifier.Text);

        var variable = Assert.IsType<VarStatementSyntax>(result.Root.Members[2]);
        var variableType = Assert.IsType<NamedTypeSyntax>(variable.Type);
        Assert.Equal("Box", variableType.Identifier.Text);
        Assert.Equal("Int", Assert.IsType<NamedTypeSyntax>(Assert.Single(variableType.TypeArguments)).Identifier.Text);
        var call = Assert.IsType<CallExpressionSyntax>(variable.Initializer);
        Assert.Equal("Int", Assert.IsType<NamedTypeSyntax>(Assert.Single(call.TypeArguments)).Identifier.Text);
    }

    [Fact]
    public void ParseBraceBlocksIgnoresLeadingWhitespace()
    {
        const string code = "fn main() -> Int {\n  let value: Int = 1;\n    if value == 1 {\n print(value);\n    }\n  return 0;\n}\n";

        var result = VelaParser.Parse(new SourceText(code, "braces.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        Assert.Equal(3, function.Body.Statements.Count);
        Assert.IsType<IfStatementSyntax>(function.Body.Statements[1]);
    }

    [Fact]
    public void ParseMissingSemicolonReportsParserDiagnostic()
    {
        const string code = "fn main() -> Int {\n    let value: Int = 1\n    return value;\n}\n";

        var result = VelaParser.Parse(new SourceText(code, "semicolon.vela"));

        var diagnostic = Assert.Single(result.Diagnostics, static item => item.Code == "P002");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new TextLocation("semicolon.vela", 2, 23), new SourceText(code, "semicolon.vela").GetLocation(diagnostic.Span));
    }

    [Fact]
    public void ParseIncludeAndObjectDeclarationsCaptureBraceSyntax()
    {
        const string code = """
            include vela.math as math;

            interface Printable {
                fn render() -> Text;
            }

            class Counter implements Printable {
                var value: Int;
                fn render() -> Text {
                    return "Counter";
                }
            }
            """;

        var result = VelaParser.Parse(new SourceText(code, "objects.vela"));

        Assert.Empty(result.Diagnostics);
        var include = Assert.IsType<IncludeDirectiveSyntax>(result.Root.Members[0]);
        Assert.Equal("vela.math", include.PackageName);
        Assert.Equal("math", include.Alias!.Text);
        var contract = Assert.IsType<ObjectDeclarationSyntax>(result.Root.Members[1]);
        Assert.Equal(ObjectDeclarationKind.Interface, contract.Kind);
        var implementation = Assert.IsType<ObjectDeclarationSyntax>(result.Root.Members[2]);
        Assert.Equal(ObjectDeclarationKind.Class, implementation.Kind);
        Assert.Equal("Printable", Assert.IsType<NamedTypeSyntax>(Assert.Single(implementation.ImplementedInterfaces)).Identifier.Text);
    }

    [Fact]
    public void ParseInvalidCharacterReportsSourceMappedLexerDiagnostic()
    {
        const string code = "let value = @;";
        var source = new SourceText(code, "invalid-character.vela");

        var result = VelaParser.Parse(source);

        var diagnostic = Assert.Single(result.Diagnostics, static item => item.Code == "L001");
        Assert.Equal(12, diagnostic.Span.Start);
        Assert.Equal(1, diagnostic.Span.Length);

        var rendered = DiagnosticFormatter.Format(source, diagnostic);
        Assert.Contains("invalid-character.vela:1:13", rendered, StringComparison.Ordinal);
        Assert.Contains("Unexpected character '@'.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMultipleIndependentErrorsRecoversAndContinuesParsing()
    {
        const string code = "fn bad(value Int) {\n  return @;\n}\nlet = 42;\n";

        var result = VelaParser.Parse(new SourceText(code, "recovery.vela"));

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Root.Members.Count);
        Assert.Contains(result.Diagnostics, static item => item.Code == "L001");
        Assert.Contains(result.Diagnostics, static item => item.Code == "P001");
        Assert.Contains(result.Diagnostics, static item => item.Code == "P004");
    }

    [Fact]
    public void ParseAssertionCapturesConditionAndMessage()
    {
        const string code = "fn main() -> Int {\n    assert 4 > 0, \"positive\";\n    return 0;\n}\n";

        var result = VelaParser.Parse(new SourceText(code, "assertion.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        var assertion = Assert.IsType<AssertStatementSyntax>(function.Body.Statements[0]);
        Assert.IsType<BinaryExpressionSyntax>(assertion.Condition);
        var message = Assert.IsType<LiteralExpressionSyntax>(assertion.Message);
        Assert.Equal("positive", message.LiteralToken.Value);
    }

    [Fact]
    public void ParseCollectionsCapturesGenericConstructionIndexingAndIteration()
    {
        const string code = "fn main() -> Int {\n    var values = Vector<Int>(4);\n    values[0] = 7;\n    for value in values {\n        print(value);\n    }\n    return 0;\n}\n";

        var result = VelaParser.Parse(new SourceText(code, "collections.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        var variable = Assert.IsType<VarStatementSyntax>(function.Body.Statements[0]);
        var construction = Assert.IsType<CallExpressionSyntax>(variable.Initializer);
        Assert.Equal("Int", Assert.IsType<NamedTypeSyntax>(Assert.Single(construction.TypeArguments)).Identifier.Text);

        var assignment = Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<IndexExpressionSyntax>(Assert.IsType<AssignmentExpressionSyntax>(assignment.Expression).Target);

        var loop = Assert.IsType<ForStatementSyntax>(function.Body.Statements[2]);
        Assert.Equal("value", loop.Identifier.Text);
        Assert.IsType<NameExpressionSyntax>(loop.Collection);
    }

    [Fact]
    public void ParseWhileLoopControlAndSwitchCapturesBraceBasedControlFlow()
    {
        const string code = """
            fn main() -> Int {
                var value: Int = 0;
                while value < 10 {
                    value = value + 1;
                    if value == 3 {
                        continue;
                    }
                    if value == 8 {
                        break;
                    }
                }
                switch value {
                    case 8 {
                        print("stopped");
                    }
                    default {
                        print("other");
                    }
                }
                return value;
            }
            """;

        var result = VelaParser.Parse(new SourceText(code, "control-flow.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        var loop = Assert.IsType<WhileStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<ContinueStatementSyntax>(Assert.IsType<IfStatementSyntax>(loop.Body.Statements[1]).ThenBlock.Statements[0]);
        Assert.IsType<BreakStatementSyntax>(Assert.IsType<IfStatementSyntax>(loop.Body.Statements[2]).ThenBlock.Statements[0]);
        var selection = Assert.IsType<SwitchStatementSyntax>(function.Body.Statements[2]);
        Assert.Single(selection.Cases);
        Assert.NotNull(selection.DefaultClause);
    }

    [Fact]
    public void ParseDuplicateSwitchDefaultReportsDiagnostic()
    {
        const string code = "switch 1 { default { print(\"first\"); } default { print(\"second\"); } }";

        var result = VelaParser.Parse(new SourceText(code, "duplicate-default.vela"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "P008");
    }

    [Fact]
    public void LexerPreservesNestedAndDocumentationCommentsWithoutChangingGrammarTokens()
    {
        const string code = "/* outer /* inner */ outer */\n/// Adds two values.\n/// @param left First value.\nfn add(left: Int, right: Int) -> Int { return left + right; }\n";

        var result = new VelaLexer(new SourceText(code, "comments.vela")).Lex();

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Trivia.Count);
        Assert.Equal(SyntaxTriviaKind.BlockComment, result.Trivia[0].Kind);
        Assert.All(result.Trivia.Skip(1), static trivia => Assert.True(trivia.IsDocumentation));
        Assert.DoesNotContain(result.Tokens, static token => token.Text.Contains("Adds", StringComparison.Ordinal));

        var parsed = VelaParser.Parse(new SourceText(code, "comments.vela"));
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(parsed.Root.Members));
        Assert.NotNull(function.Documentation);
        Assert.Contains("Adds two values.", function.Documentation!.Text, StringComparison.Ordinal);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void ParseDocumentationAndEnumsAttachMetadataAndValidateKnownTags()
    {
        const string code = "/// @param missing Not a declared argument.\nfn run(value: Int) -> Unit { return; }\n\n/** A lifecycle. */\nenum State { /// Ready state.\nReady,\nRunning;\n}\n";

        var result = VelaParser.Parse(new SourceText(code, "enum-docs.vela"));

        var function = Assert.IsType<FunctionDeclarationSyntax>(result.Root.Members[0]);
        var declaration = Assert.IsType<EnumDeclarationSyntax>(result.Root.Members[1]);
        Assert.NotNull(function.Documentation);
        Assert.Equal("A lifecycle.", declaration.Documentation!.Text);
        Assert.NotNull(declaration.Members[0].Documentation);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "DOC002");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ParseUnterminatedBlockCommentReportsLexerDiagnostic()
    {
        var result = VelaParser.Parse(new SourceText("/* never closes", "unterminated-comment.vela"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "L009");
    }

    [Fact]
    public void ParseDeferAndTypedTryCatchFinallyCapturesBlockStructure()
    {
        const string code = """
            fn main() -> Int {
                defer cleanup(1);
                try {
                    return 0;
                }
                catch VelaIoException error {
                    print(error.message);
                    return 1;
                }
                finally {
                    print("finished");
                }
            }
            """;

        var result = VelaParser.Parse(new SourceText(code, "exceptions.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        var defer = Assert.IsType<DeferStatementSyntax>(function.Body.Statements[0]);
        Assert.IsType<CallExpressionSyntax>(defer.Invocation);
        var protectedStatement = Assert.IsType<TryStatementSyntax>(function.Body.Statements[1]);
        var handler = Assert.Single(protectedStatement.Catches);
        Assert.Equal("VelaIoException", Assert.IsType<NamedTypeSyntax>(handler.ExceptionType).Identifier.Text);
        Assert.Equal("error", handler.Identifier.Text);
        Assert.NotNull(protectedStatement.FinallyClause);
    }

    [Fact]
    public void ParseAsyncFunctionAndAwaitCapturesFutureSyntax()
    {
        const string code = """
            async fn load() -> Int {
                let value = await fetch();
                return value;
            }
            """;

        var result = VelaParser.Parse(new SourceText(code, "async.vela"));

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
        Assert.NotNull(function.AsyncKeyword);
        var variable = Assert.IsType<LetStatementSyntax>(function.Body.Statements[0]);
        Assert.IsType<AwaitExpressionSyntax>(variable.Initializer);
    }
}
