using Vela.Backend;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaReplSessionTests
{
    [Fact]
    public void BalancedInputNeedsNoContinuation()
    {
        Assert.False(VelaReplSession.ExpectsMore("let answer: Int = 40 + 2;"));
        Assert.False(VelaReplSession.ExpectsMore("}"));
        Assert.False(VelaReplSession.ExpectsMore(string.Empty));
    }

    [Fact]
    public void OpenBraceNeedsContinuation()
    {
        Assert.True(VelaReplSession.ExpectsMore("fn main() -> Int {"));
        Assert.True(VelaReplSession.ExpectsMore("if value == 1 {\n    return 1;"));
        Assert.False(VelaReplSession.ExpectsMore("fn main() -> Int {\n    return 0;\n}"));
    }

    [Fact]
    public void BracesInStringsAndCommentsDoNotCount()
    {
        Assert.False(VelaReplSession.ExpectsMore("print(\"{ not a brace\");"));
        Assert.False(VelaReplSession.ExpectsMore("// { not a brace"));
        Assert.False(VelaReplSession.ExpectsMore("/* { not a brace */"));
    }

    [Fact]
    public void PushPopClearTrackSource()
    {
        var session = new VelaReplSession();

        Assert.Equal(0, session.Count);
        Assert.Equal(string.Empty, session.Source);

        session.Push("let answer: Int = 40 + 2;");
        session.Push("print(answer);");

        Assert.Equal(2, session.Count);
        Assert.Equal("let answer: Int = 40 + 2;\nprint(answer);\n", session.Source);

        Assert.True(session.Pop());
        Assert.Equal("let answer: Int = 40 + 2;\n", session.Source);

        session.Clear();
        Assert.Equal(0, session.Count);
        Assert.False(session.Pop());
    }
}
