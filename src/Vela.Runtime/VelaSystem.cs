using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Vela.Runtime;

/// <summary>Contains the bounded result of a child process.</summary>
public sealed record VelaProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Truncated);

/// <summary>Provides safe process and host-system primitives without invoking a shell.</summary>
public static class VelaSystem
{
    private const int MaximumTimeoutMilliseconds = 600_000;
    private const int MaximumCaptureBytes = 16 * 1024 * 1024;

    /// <summary>Executes a child process with bounded time and captured output without invoking a shell.</summary>
    /// <param name="program">The executable name or path.</param>
    /// <param name="arguments">The arguments passed directly to the executable.</param>
    /// <param name="timeoutMilliseconds">The maximum execution time.</param>
    /// <param name="maximumOutputBytes">The maximum UTF-8 byte count captured from each output stream.</param>
    /// <param name="sourceLocation">The originating Vela source location.</param>
    /// <returns>The process exit status and bounded output.</returns>
    /// <exception cref="VelaProcessException">The options are invalid or the process cannot be started or terminated.</exception>
    public static VelaProcessResult Exec(
        string program,
        IEnumerable<string> arguments,
        int timeoutMilliseconds,
        int maximumOutputBytes,
        string sourceLocation)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            throw new VelaProcessException("Process program cannot be empty.", sourceLocation);
        }

        ArgumentNullException.ThrowIfNull(arguments);
        if (timeoutMilliseconds is < 1 or > MaximumTimeoutMilliseconds)
        {
            throw new VelaProcessException($"Process timeout must be between 1 and {MaximumTimeoutMilliseconds} milliseconds.", sourceLocation);
        }

        if (maximumOutputBytes is < 1 or > MaximumCaptureBytes)
        {
            throw new VelaProcessException($"Process output limit must be between 1 and {MaximumCaptureBytes} bytes.", sourceLocation);
        }

        try
        {
            return ExecAsync(program, arguments, timeoutMilliseconds, maximumOutputBytes).GetAwaiter().GetResult();
        }
        catch (VelaProcessException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            throw new VelaProcessException($"Unable to execute process '{program}'.", sourceLocation, exception);
        }
    }

    /// <summary>Finds an executable using the host operating system's path conventions.</summary>
    /// <param name="program">The executable name or path.</param>
    /// <returns>The absolute executable path when found; otherwise, an empty option.</returns>
    public static Option<string> Which(string program)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return Option.None<string>();
        }

        if (program.Contains(Path.DirectorySeparatorChar)
            || program.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(program) ? Option.Some(Path.GetFullPath(program)) : Option.None<string>();
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() && Path.GetExtension(program).Length == 0 ? program + extension : program);
                if (File.Exists(candidate))
                {
                    return Option.Some(Path.GetFullPath(candidate));
                }
            }
        }

        return Option.None<string>();
    }

    /// <summary>Returns the identifier of the current Vela process.</summary>
    /// <returns>The current process identifier.</returns>
    public static int ProcessId() => Environment.ProcessId;

    /// <summary>Returns the host temporary-directory root.</summary>
    /// <returns>The host temporary-directory path.</returns>
    public static string TemporaryDirectory() => Path.GetTempPath();

    private static async Task<VelaProcessResult> ExecAsync(
        string program,
        IEnumerable<string> arguments,
        int timeoutMilliseconds,
        int maximumOutputBytes)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Process '{program}' did not start.");
        }

        var outputTask = CaptureAsync(process.StandardOutput, maximumOutputBytes);
        var errorTask = CaptureAsync(process.StandardError, maximumOutputBytes);
        var timedOut = false;
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and termination request.
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new VelaProcessResult(
            timedOut ? -1 : process.ExitCode,
            output.Text,
            error.Text,
            timedOut,
            output.Truncated || error.Truncated);
    }

    private static async Task<CapturedText> CaptureAsync(StreamReader reader, int maximumBytes)
    {
        var builder = new StringBuilder(Math.Min(maximumBytes, 4096));
        var buffer = new char[4096];
        var capturedBytes = 0;
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (capturedBytes >= maximumBytes)
            {
                truncated = true;
                continue;
            }

            var available = maximumBytes - capturedBytes;
            var span = buffer.AsSpan(0, count);
            var bytes = Encoding.UTF8.GetByteCount(span);
            if (bytes <= available)
            {
                builder.Append(span);
                capturedBytes += bytes;
                continue;
            }

            var length = FindFittingCharacterCount(span, available);
            builder.Append(span[..length]);
            capturedBytes += Encoding.UTF8.GetByteCount(span[..length]);
            truncated = true;
        }

        return new CapturedText(builder.ToString(), truncated);
    }

    private static int FindFittingCharacterCount(ReadOnlySpan<char> value, int maximumBytes)
    {
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value[..middle]) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]) && char.IsLowSurrogate(value[low]))
        {
            low--;
        }

        return low;
    }

    private sealed record CapturedText(string Text, bool Truncated);
}
