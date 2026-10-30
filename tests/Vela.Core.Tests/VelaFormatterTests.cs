using Vela.Core.Formatting;
using Vela.Core.Parsing;
using Vela.Core.Source;
using Xunit;

namespace Vela.Core.Tests;

public sealed class VelaFormatterTests
{
    [Fact]
    public void FormatNormalizesSpacingAndIndentation()
    {
        const string code = "fn   main( )  ->  Int {\nlet x:Int=1+2;\nif(x>1){\nprint( \"hi\" );}\nreturn x;\n}\n";

        var result = VelaFormatter.Format(new SourceText(code, "messy.vela"));

        Assert.False(result.HasErrors);
        Assert.Equal(
            "fn main() -> Int {\n    let x: Int = 1 + 2;\n    if (x>1) {\n        print(\"hi\");\n    }\n    return x;\n}\n",
            result.Text);
    }

    [Fact]
    public void FormatKeepsElseOnSameLineAsClosingBrace()
    {
        const string code = "fn f(value: Int) -> Int {\n    if value == 1 {\n        return 1;\n    } else {\n        return 0;\n    }\n}\n";

        var result = VelaFormatter.Format(new SourceText(code, "else.vela"));

        Assert.False(result.HasErrors);
        Assert.Contains("} else {", result.Text, StringComparison.Ordinal);
        Assert.Equal(code, result.Text);
    }

    [Fact]
    public void FormatPreservesCommentsVerbatim()
    {
        const string code = """
            # Hash comment
            // Line comment
            /// Doc comment
            /* Block comment */
            /** Doc block */
            /* outer /* nested */ still comment */
            fn main() -> Int {
                let message: Text = "Hello"; // trailing
                return 0;
            }
            """;

        var result = VelaFormatter.Format(new SourceText(code + "\n", "comments.vela"));

        Assert.False(result.HasErrors);
        Assert.Contains("# Hash comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("// Line comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("/// Doc comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("/* Block comment */", result.Text, StringComparison.Ordinal);
        Assert.Contains("/** Doc block */", result.Text, StringComparison.Ordinal);
        Assert.Contains("/* outer /* nested */ still comment */", result.Text, StringComparison.Ordinal);
        Assert.Contains("// trailing", result.Text, StringComparison.Ordinal);
        Assert.Equal(result.Text, VelaFormatter.Format(new SourceText(result.Text, "comments.vela")).Text);
    }

    [Fact]
    public void FormatPreservesGenericAndNullableSpacing()
    {
        const string code = """
            record Box<T> {
                value: T;
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main() -> Int {
                var boxed: Box<Int> = Box<Int>(identity<Int>(1));
                let optional: Int? = boxed.value;
                let negative: Int = -1;
                let same: Bool = optional == 1;
                return negative;
            }
            """;

        var result = VelaFormatter.Format(new SourceText(code + "\n", "generics.vela"));

        Assert.False(result.HasErrors);
        Assert.Contains("Box<Int>", result.Text, StringComparison.Ordinal);
        Assert.Contains("identity<Int>(1)", result.Text, StringComparison.Ordinal);
        Assert.Contains("Int?", result.Text, StringComparison.Ordinal);
        Assert.Contains("= -1;", result.Text, StringComparison.Ordinal);
        Assert.Equal(result.Text, VelaFormatter.Format(new SourceText(result.Text, "generics.vela")).Text);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void FormatIsIdempotentOnRealisticSource(string newline)
    {
        const string code = """
            include vela.core;

            fn factorial(value: Int) -> Int {
                if value <= 1 {
                    return 1;
                } else {
                    return value * factorial(value - 1);
                }
            }

            fn main() -> Int {
                var total: Int = 0;
                for level in [1, 2, 3] {
                    total = total + factorial(level);
                }

                switch total {
                    case 9 {
                        print("nine");
                    }
                    default {
                        print(total);
                    }
                }

                return 0;
            }
            """;

        var once = VelaFormatter.Format(new SourceText(code.ReplaceLineEndings(newline) + newline, "program.vela"));

        Assert.False(once.HasErrors);
        Assert.Equal(code.ReplaceLineEndings("\n") + "\n", once.Text);
        Assert.Equal(once.Text, VelaFormatter.Format(new SourceText(once.Text, "program.vela")).Text);
    }

    [Fact]
    public void FormatKeepsRecordPatternsOnOneLine()
    {
        const string code = """
            record ServerConfig {
                host: Text;
                port: Int;
            }

            fn main() -> Void {
                let (name, workers) = ("api", 8);
                let config = ServerConfig("localhost", 8080);
                let ServerConfig { host, port } = config;
                print(host);
                print(port);
            }
            """;

        var before = VelaParser.Parse(new SourceText(code + "\n", "destructuring.vela"));
        var result = VelaFormatter.Format(new SourceText(code + "\n", "destructuring.vela"));
        var after = VelaParser.Parse(new SourceText(result.Text, "destructuring.vela"));

        Assert.False(result.HasErrors);
        Assert.Empty(before.Diagnostics);
        Assert.Empty(after.Diagnostics);
        Assert.Contains("let ServerConfig { host, port } = config;", result.Text, StringComparison.Ordinal);
        Assert.Equal(before.Root.Members.Count, after.Root.Members.Count);
    }

    [Fact]
    public void FormatRefusesLexErrorsWithoutChangingText()
    {
        const string code = "fn main() -> Int {\n    let broken: Text = \"oops;\n    return 0;\n}\n";

        var result = VelaFormatter.Format(new SourceText(code, "broken.vela"));

        Assert.True(result.HasErrors);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(code, result.Text);
    }

    [Fact]
    public void FormatCollapsesBlankLinesAndEndsWithSingleNewline()
    {
        var result = VelaFormatter.Format(new SourceText("fn main() -> Int {\n\n\n    return 0;\n}\n\n", "blanks.vela"));

        Assert.False(result.HasErrors);
        Assert.Equal("fn main() -> Int {\n\n    return 0;\n}\n", result.Text);
        Assert.Equal(string.Empty, VelaFormatter.Format(new SourceText("   \n  ", "empty.vela")).Text);
    }
}
