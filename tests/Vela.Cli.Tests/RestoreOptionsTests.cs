using Xunit;

namespace Vela.Cli.Tests;

public sealed class RestoreOptionsTests
{
    [Fact]
    public void RestoreRequiresTrustByDefault()
    {
        var options = CommandOptions.Parse(["package", "restore", "example.lib"]);
        Assert.False(options.AllowUnsigned);
        Assert.Null(options.TrustedRootPath);
    }

    [Fact]
    public void TrustedRootIsAnExplicitLocalPath()
    {
        var options = CommandOptions.Parse(["package", "restore", "example.lib", "--trusted-root", "trusted-root.json"]);
        Assert.Equal(Path.GetFullPath("trusted-root.json"), options.TrustedRootPath);
        Assert.False(options.AllowUnsigned);
    }

    [Fact]
    public void UnsignedRestoreMustBeExplicit()
    {
        var options = CommandOptions.Parse(["package", "restore", "example.lib", "--allow-unsigned"]);
        Assert.True(options.AllowUnsigned);
        Assert.Null(options.TrustedRootPath);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("build")]
    [InlineData("run")]
    public void TrustOptionsAreRejectedOutsideRestore(string command)
    {
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse([command, "main.vela", "--allow-unsigned"]));
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse([command, "main.vela", "--trusted-root", "root.json"]));
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("push")]
    [InlineData("login")]
    [InlineData("logout")]
    public void TrustOptionsAreRejectedByOtherPackageCommands(string command)
    {
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse(["package", command, "--allow-unsigned"]));
    }

    [Fact]
    public void ContradictoryTrustModesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse(
            ["package", "restore", "example.lib", "--trusted-root", "root.json", "--allow-unsigned"]));
    }

    [Fact]
    public void TrustedRootRequiresAValue()
    {
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse(["package", "restore", "example.lib", "--trusted-root"]));
        Assert.Throws<ArgumentException>(() => CommandOptions.Parse(["package", "restore", "example.lib", "--trusted-root", "--allow-unsigned"]));
    }
}
