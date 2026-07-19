using Vela.Backend;
using Vela.Backend.Abi;
using Vela.Backend.Capabilities;
using Vela.Core.Source;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class CallbackAndCapabilityTests
{
    [Fact]
    public void CapturingLambdaEmitsTypedDelegate()
    {
        var compilation = VelaCompiler.Compile(new SourceText("""
            fn main() -> Int {
                let prefix = "Hello ";
                let format: Fn<(Text), Text> = fn(name: Text) -> Text {
                    return prefix + name;
                };
                print(format("Vela"));
                return 0;
            }
            """, "callback.vela"));

        Assert.False(compilation.HasErrors);
        Assert.Contains("Func<string, string>", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("__velaCb", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogLoadsAndRejectsUnknownCapability()
    {
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eng", "capabilities", "vela-capabilities.json"));
        if (!File.Exists(catalogPath))
        {
            catalogPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "eng", "capabilities", "vela-capabilities.json"));
        }

        Assert.True(File.Exists(catalogPath), $"Missing catalog at {catalogPath}");
        var catalog = VelaCapabilityCatalog.Load(catalogPath);
        Assert.Throws<VelaCapabilityException>(() => catalog.Resolve(["unknown"]));
        var resolved = catalog.Resolve(["sqlite", "aspnet-server"]);
        Assert.Equal(2, resolved.Count);
        Assert.Equal("sqlite", resolved[0].Id);
    }

    [Fact]
    public void CompileConsumerBindsTextAndDecimalLibraryImport()
    {
        var manifest = VelaAbiManifest.Create(
            "vela.echo",
            "0.1.0",
            "win-x64",
            "vela_echo.dll",
            [
                new VelaFfiExport("echo", "vela_vela_echo_echo", ["Text"], "Text"),
                new VelaFfiExport("scale", "vela_vela_echo_scale", ["Decimal"], "Decimal")
            ]);
        var compilation = VelaCompiler.Compile(
            new SourceText("""
                include vela.echo as echo;
                fn main() -> Int {
                    print(echo.echo("hi"));
                    return 0;
                }
                """, "app.vela"),
            [new VelaLibraryImport("vela.echo", "vela_echo.dll", manifest)]);

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static item => item.Message)));
        Assert.Contains("VelaText.FromString", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaDecimal.FromDecimal", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("ToManagedString()", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AbiHeaderWriterEmitsTextAndDecimalTypes()
    {
        var manifest = VelaAbiManifest.Create(
            "acme.echo",
            "1.0.0",
            "win-x64",
            "acme_echo.dll",
            [new VelaFfiExport("echo", "vela_acme_echo_echo", ["Text"], "Text")]);
        var header = VelaAbiHeaderWriter.Write(manifest);
        Assert.Contains("typedef struct vela_text", header, StringComparison.Ordinal);
        Assert.Contains("vela_acme_echo_echo", header, StringComparison.Ordinal);
    }

    [Fact]
    public void SqliteModuleMarksRequiresSqlite()
    {
        var compilation = VelaCompiler.Compile(new SourceText("""
            include vela.core.sqlite as db;
            fn main() -> Void {
                let database = db.open(":memory:");
                db.close(database);
            }
            """, "sqlite.vela"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static item => item.Message)));
        Assert.True(compilation.RequiresSqlite);
        Assert.Contains("using Vela.Sqlite;", compilation.GeneratedSource, StringComparison.Ordinal);
    }
}
