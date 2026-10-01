namespace Vela.Backend;

/// <summary>Finds the test files owned by one test input.</summary>
/// <remarks>
/// A test file is any <c>*_test.vela</c> script: a self-contained program that
/// reports success with exit code zero (for example through the core
/// <c>assert</c> statement or the <c>vela.std.test</c> helpers). Files under a
/// <c>target</c> directory are build output and are never collected.
/// </remarks>
public static class VelaTestDiscovery
{
    /// <summary>Returns the sorted test files under <paramref name="directory"/>.</summary>
    public static string[] FindTestFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetFullPath(directory);
        return Directory.EnumerateFiles(root, "*_test.vela", SearchOption.AllDirectories)
            .Where(static path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("target", StringComparer.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }
}
