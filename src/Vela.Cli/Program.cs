using Vela.Backend;
using Vela.Core.Diagnostics;
using Vela.Core.Source;

return await VelaCommandLine.RunAsync(args);

internal static class VelaCommandLine
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] is "--help" or "-h" or "help")
            {
                PrintUsage();
                return 0;
            }

            return arguments[0] switch
            {
                "check" => Check(arguments[1..]),
                "run" => await RunProgramAsync(arguments[1..]),
                "build" => await BuildAsync(arguments[1..]),
                _ => Fail($"Unknown command '{arguments[0]}'. Run 'vela --help' for usage.")
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("The operation was cancelled.");
        }
        catch (Exception exception)
        {
            return Fail($"Internal compiler failure: {exception.Message}");
        }
    }

    private static int Check(string[] arguments)
    {
        if (!TryGetInputPath(arguments, out var inputPath))
        {
            return Fail("The check command requires one .vela source file.");
        }

        var compilation = Compile(inputPath);
        PrintDiagnostics(compilation);
        if (compilation.HasErrors)
        {
            return 1;
        }

        Console.WriteLine($"Check succeeded: {inputPath}");
        return 0;
    }

    private static async Task<int> RunProgramAsync(string[] arguments)
    {
        if (!TryGetInputPath(arguments, out var inputPath))
        {
            return Fail("The run command requires one .vela source file.");
        }

        var compilation = Compile(inputPath);
        PrintDiagnostics(compilation);
        if (compilation.HasErrors)
        {
            return 1;
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "vela", Guid.NewGuid().ToString("N"));
        try
        {
            var buildService = new VelaBuildService(FindRuntimeProject());
            var generatedProject = buildService.WriteSourceProject(
                compilation,
                new BuildOptions(Path.GetFileNameWithoutExtension(inputPath), temporaryDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(generatedProject);
            WriteProcessOutput(result);
            return result.ExitCode;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task<int> BuildAsync(string[] arguments)
    {
        if (!TryGetInputPath(arguments, out var inputPath))
        {
            return Fail("The build command requires one .vela source file.");
        }

        if (!TryGetOption(arguments, "--output", out var outputDirectory))
        {
            return Fail("The build command requires '--output <directory>'.");
        }

        var target = TryGetOption(arguments, "--target", out var specifiedTarget) ? specifiedTarget : "win-x64";
        var mode = TryGetOption(arguments, "--mode", out var specifiedMode)
            ? ParseMode(specifiedMode)
            : ExecutableMode.NativeAot;
        if (mode is null)
        {
            return Fail("The build mode must be 'native-aot', 'single-file', or 'framework-dependent'.");
        }

        var compilation = Compile(inputPath);
        PrintDiagnostics(compilation);
        if (compilation.HasErrors)
        {
            return 1;
        }

        var buildService = new VelaBuildService(FindRuntimeProject());
        var result = await buildService.BuildAsync(
            compilation,
            new BuildOptions(Path.GetFileNameWithoutExtension(inputPath), outputDirectory, target, mode.Value));
        WriteProcessOutput(new ProcessResult(result.Succeeded ? 0 : 1, result.StandardOutput, result.StandardError));
        if (result.Succeeded)
        {
            Console.WriteLine($"Build succeeded: {result.PublishDirectory}");
            Console.WriteLine($"Generated source: {result.SourceDirectory}");
            return 0;
        }

        return 1;
    }

    private static VelaCompilation Compile(string inputPath)
    {
        var source = new SourceText(File.ReadAllText(inputPath), inputPath);
        return VelaCompiler.Compile(source);
    }

    private static void PrintDiagnostics(VelaCompilation compilation)
    {
        foreach (var diagnostic in compilation.Diagnostics.OrderBy(static diagnostic => diagnostic.Span.Start))
        {
            Console.Error.WriteLine(DiagnosticFormatter.Format(compilation.Source, diagnostic));
        }
    }

    private static void WriteProcessOutput(ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            Console.Out.Write(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            Console.Error.Write(result.StandardError);
        }
    }

    private static bool TryGetInputPath(string[] arguments, out string inputPath)
    {
        inputPath = string.Empty;
        if (arguments.Length == 0 || arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = Path.GetFullPath(arguments[0]);
        if (!File.Exists(candidate))
        {
            _ = Fail($"Source file not found: {candidate}");
            return false;
        }

        if (!string.Equals(Path.GetExtension(candidate), ".vela", StringComparison.OrdinalIgnoreCase))
        {
            _ = Fail("The source file must use the .vela extension.");
            return false;
        }

        inputPath = candidate;
        return true;
    }

    private static bool TryGetOption(string[] arguments, string option, out string value)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                value = arguments[index + 1];
                return !value.StartsWith("--", StringComparison.Ordinal);
            }
        }

        value = string.Empty;
        return false;
    }

    private static ExecutableMode? ParseMode(string value) => value switch
    {
        "native-aot" => ExecutableMode.NativeAot,
        "single-file" => ExecutableMode.SingleFile,
        "framework-dependent" => ExecutableMode.FrameworkDependent,
        _ => null
    };

    private static string FindRuntimeProject()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src", "Vela.Runtime", "Vela.Runtime.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("Unable to locate src/Vela.Runtime/Vela.Runtime.csproj. Run the CLI from the Vela repository.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error VEL9000: {message}");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Vela compiler");
        Console.WriteLine("  vela check <file.vela>");
        Console.WriteLine("  vela run <file.vela>");
        Console.WriteLine("  vela build <file.vela> --output <directory> [--target win-x64] [--mode native-aot|single-file|framework-dependent]");
    }
}
