using Vela.Core.Analysis;
using Vela.Core.Diagnostics;
using Vela.Core.Parsing;
using Vela.Core.Source;
using Xunit;

namespace Vela.Core.Tests;

public sealed class VelaLinterTests
{
    [Fact]
    public void BooleanComparisonsAreReported()
    {
        const string code = """
            fn check(flag: Bool) -> Int {
                if flag == true {
                    return 1;
                }
                if flag != false {
                    return 2;
                }
                return 0;
            }
            """;

        var findings = Analyze(code);

        Assert.Equal(["VET1001", "VET1001"], findings.Select(static finding => finding.Code).ToArray());
        Assert.All(findings, static finding => Assert.Equal(DiagnosticSeverity.Warning, finding.Severity));
    }

    [Fact]
    public void EmptyBlocksAreReported()
    {
        const string code = """
            fn empty() -> Void {
            }

            fn main() -> Int {
                if true {
                }
                return 0;
            }
            """;

        var findings = Analyze(code);

        Assert.Equal(["VET1002", "VET1002"], findings.Select(static finding => finding.Code).ToArray());
    }

    [Fact]
    public void UnreachableStatementsAreReported()
    {
        const string code = """
            fn main() -> Int {
                return 0;
                print("dead");
            }

            fn loop() -> Void {
                while true {
                    break;
                    print("dead");
                }
            }
            """;

        var findings = Analyze(code);

        Assert.Equal(["VET1003", "VET1003"], findings.Select(static finding => finding.Code).ToArray());
    }

    [Fact]
    public void NestedFindingsReachThroughLambdasAndObjects()
    {
        const string code = """
            fn apply(value: Int, action: Fn<(Int), Int>) -> Int {
                return action(value);
            }

            fn main() -> Int {
                let doubled = apply(21, fn(value: Int) -> Int {
                    if value == true {
                        return value;
                    }
                    return value * 2;
                });
                return doubled;
            }
            """;

        var findings = Analyze(code);

        var single = Assert.Single(findings);
        Assert.Equal("VET1001", single.Code);
        Assert.Contains("boolean", single.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanCodeHasNoFindings()
    {
        const string code = """
            fn factorial(value: Int) -> Int {
                if value <= 1 {
                    return 1;
                } else {
                    return value * factorial(value - 1);
                }
            }
            """;

        var root = VelaParser.Parse(new SourceText(code, "clean.vela"));

        Assert.Empty(root.Diagnostics);
        Assert.Empty(VelaLinter.Analyze(root.Root));
    }

    private static List<Diagnostic> Analyze(string code)
    {
        var parsed = VelaParser.Parse(new SourceText(code, "vet.vela"));

        Assert.Empty(parsed.Diagnostics);
        return VelaLinter.Analyze(parsed.Root).ToList();
    }
}
