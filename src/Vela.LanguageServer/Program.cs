using System.Text.Json;
using Vela.LanguageServer;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Contains("--stdio", StringComparer.Ordinal))
        {
            var server = new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
            await server.RunAsync().ConfigureAwait(false);
            return 0;
        }

        var checkFileIndex = Array.IndexOf(arguments, "--check-file");
        if (checkFileIndex >= 0)
        {
            var filePath = checkFileIndex + 1 < arguments.Length ? arguments[checkFileIndex + 1] : null;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Console.Error.WriteLine("--check-file requires a path argument.");
                return 2;
            }

            return CheckFile(filePath);
        }

        Console.Error.WriteLine("Usage: vela-lsp --stdio | --check-file <path>");
        return 2;
    }

    private static int CheckFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 2;
        }

        var compilation = VelaWorkspace.CompileFile(filePath);
        var reports = VelaWorkspace.BuildDiagnosticReports(compilation);
        Console.WriteLine(JsonSerializer.Serialize(reports, JsonOptions));
        return 0;
    }
}
