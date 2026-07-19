using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Vela.LanguageServer.Tests;

public sealed class CheckFileCommandTests
{
    [Fact]
    public async Task CheckFileValidHelloExampleReturnsEmptyDiagnostics()
    {
        var helloPath = Path.Combine(FindRepositoryRoot(), "examples", "hello.vela");
        var output = await RunLanguageServerAsync("--check-file", helloPath);
        var diagnostics = JsonSerializer.Deserialize<DiagnosticDto[]>(output, JsonOptions);

        Assert.NotNull(diagnostics);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CheckFileInvalidSyntaxReturnsAtLeastOneDiagnostic()
    {
        var diagnosticsPath = Path.Combine(FindRepositoryRoot(), "examples", "diagnostics.vela");
        var output = await RunLanguageServerAsync("--check-file", diagnosticsPath);
        var diagnostics = JsonSerializer.Deserialize<DiagnosticDto[]>(output, JsonOptions);

        Assert.NotNull(diagnostics);
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<string> RunLanguageServerAsync(params string[] arguments)
    {
        var projectPath = Path.GetFullPath(Path.Combine(FindRepositoryRoot(), "src", "Vela.LanguageServer", "Vela.LanguageServer.csproj"));
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start vela-lsp process.");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        Assert.True(process.ExitCode == 0, $"vela-lsp failed with exit code {process.ExitCode}: {stderr}");
        return stdout.Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vela.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record DiagnosticDto(
        string Code,
        string Severity,
        string Message,
        string File,
        int Line,
        int Column,
        int EndLine,
        int EndColumn);
}
