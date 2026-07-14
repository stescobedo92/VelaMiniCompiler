using Vela.Core.Diagnostics;
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
            record Box<T>:
                value: T

            fn identity<T>(value: T) -> T:
                return value

            var result: Box<Int> = Box<Int>(identity<Int>(1))
            """;

        var result = VelaParser.Parse(new SourceText(code, "generic.vela"));

        Assert.Empty(result.Diagnostics);
        var record = Assert.IsType<RecordDeclarationSyntax>(result.Root.Members[0]);
        Assert.Equal("T", Assert.Single(record.GenericParameters).Identifier.Text);
        var field = Assert.IsType<RecordFieldSyntax>(Assert.Single(record.Members));
        Assert.Equal("T", Assert.IsType<NamedTypeSyntax>(field.Type).Identifier.Text);

        var function = Assert.IsType<FunctionDeclarationSyntax>(result.Root.Members[1]);
        var genericParameter = Assert.Single(function.GenericParameters);
        Assert.Equal("T", genericParameter.Identifier.Text);
        Assert.Equal("T", Assert.IsType<NamedTypeSyntax>(function.ReturnType).Identifier.Text);

        var variable = Assert.IsType<VarStatementSyntax>(result.Root.Members[2]);
        var variableType = Assert.IsType<NamedTypeSyntax>(variable.Type);
        Assert.Equal("Box", variableType.Identifier.Text);
        Assert.Equal("Int", Assert.IsType<NamedTypeSyntax>(Assert.Single(variableType.TypeArguments)).Identifier.Text);
        var call = Assert.IsType<CallExpressionSyntax>(variable.Initializer);
        var typeArgument = Assert.IsType<NamedTypeSyntax>(Assert.Single(call.TypeArguments));
        Assert.Equal("Int", typeArgument.Identifier.Text);
    }

    [Fact]
    public void ParseTwoSpaceIndentationReportsIndentationDiagnostic()
    {
        const string code = "fn main() -> Int:\n  return 0\n";
        var source = new SourceText(code, "indentation.vela");

        var result = VelaParser.Parse(source);

        var diagnostic = Assert.Single(result.Diagnostics, static item => item.Code == "L004");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(2, diagnostic.Span.Length);
        Assert.Equal(new TextLocation("indentation.vela", 2, 1), source.GetLocation(diagnostic.Span));
    }

    [Fact]
    public void ParseInvalidCharacterReportsSourceMappedLexerDiagnostic()
    {
        const string code = "let value = @";
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
        const string code = "fn bad(value Int):\n  return @\nlet = 42\n";

        var result = VelaParser.Parse(new SourceText(code, "recovery.vela"));

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Root.Members.Count);
        Assert.Contains(result.Diagnostics, static item => item.Code == "L004");
        Assert.Contains(result.Diagnostics, static item => item.Code == "L001");
        Assert.Contains(result.Diagnostics, static item => item.Code == "P001");
        Assert.Contains(result.Diagnostics, static item => item.Code == "P004");
    }
}
