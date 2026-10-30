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
        // The project reference stages the executable with its runtime files.
        // Running it directly exercises this build without launching an SDK
        // restore/build or an unrelated dotnet shim from the user's PATH.
        var executablePath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "vela-lsp.exe" : "vela-lsp");
        Assert.True(File.Exists(executablePath), $"Missing built language server: {executablePath}");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "vela-lsp.runtimeconfig.json")));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start vela-lsp process.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process can exit between the timeout and termination.
            }

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            throw new TimeoutException("vela-lsp did not finish within 30 seconds.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

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
