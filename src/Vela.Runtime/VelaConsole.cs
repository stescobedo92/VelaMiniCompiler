namespace Vela.Runtime;

/// <summary>Provides policy-free console primitives used by source-linked Vela packages.</summary>
public static class VelaConsole
{
    /// <summary>Writes text to the standard output stream without a line terminator.</summary>
    /// <param name="value">The text to write.</param>
    public static void Write(string value) => Console.Write(value);

    /// <summary>Writes text to the standard output stream followed by a line terminator.</summary>
    /// <param name="value">The text to write.</param>
    public static void WriteLine(string value) => Console.WriteLine(value);

    /// <summary>Writes text to the standard error stream without a line terminator.</summary>
    /// <param name="value">The text to write.</param>
    public static void WriteError(string value) => Console.Error.Write(value);

    /// <summary>Writes text to the standard error stream followed by a line terminator.</summary>
    /// <param name="value">The text to write.</param>
    public static void WriteErrorLine(string value) => Console.Error.WriteLine(value);

    /// <summary>Returns whether the standard output stream is redirected.</summary>
    /// <returns><see langword="true"/> if standard output is redirected; otherwise, <see langword="false"/>.</returns>
    public static bool IsOutputRedirected() => Console.IsOutputRedirected;

    /// <summary>Returns whether terminal color is appropriate for the current standard output stream.</summary>
    /// <returns><see langword="true"/> if color is supported and permitted; otherwise, <see langword="false"/>.</returns>
    public static bool SupportsColor()
    {
        if (Console.IsOutputRedirected || Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }
}
