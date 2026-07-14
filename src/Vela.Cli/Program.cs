using Spectre.Console;
using Vela.Backend;
using Vela.Core.Diagnostics;
using Vela.Core.Source;

return await VelaCommandLine.RunAsync(args);

internal static class VelaCommandLine
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var options = CommandOptions.Parse(arguments);
        var renderer = new VelaConsoleRenderer(options);
        try
        {
            if (options.Command is null or "help")
            {
                PrintUsage(renderer);
                return 0;
            }

            return options.Command switch
            {
                "check" => Check(options, renderer),
                "run" => await RunProgramAsync(options, renderer),
                "build" => await BuildAsync(options, renderer),
                "targets" => ShowTargets(options, renderer),
                _ => Fail(renderer, $"Unknown command '{options.Command}'. Run 'vela --help' for usage.")
            };
        }
        catch (VelaPackageException exception)
        {
            return Fail(renderer, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(renderer, "The operation was cancelled.");
        }
        catch (Exception exception)
        {
            return Fail(renderer, $"Internal compiler failure: {exception.Message}");
        }
    }

    private static int Check(CommandOptions options, VelaConsoleRenderer renderer)
    {
        var input = ResolveInput(options, renderer, out var package);
        if (input is null)
        {
            return 2;
        }

        renderer.Status("Checking", input);
        if (package is not null)
        {
            renderer.Detail("Resolving", $"{package.Packages.Count} package(s) from {package.Root.ManifestPath}");
        }

        var imports = package is null ? [] : CreateCheckImports(package, BuildTargetResolver.Resolve(BuildTargetResolver.Auto).RuntimeIdentifier);
        var compilation = Compile(input, imports);
        renderer.PrintDiagnostics(compilation);
        if (compilation.HasErrors)
        {
            return 1;
        }

        renderer.Success("Finished", $"check in {input}");
        return 0;
    }

    private static async Task<int> RunProgramAsync(CommandOptions options, VelaConsoleRenderer renderer)
    {
        var input = ResolveInput(options, renderer, out var package);
        if (input is null)
        {
            return 2;
        }

        if (package?.Root.Kind == VelaPackageKind.Library || options.BuildLibrary)
        {
            return Fail(renderer, "The run command requires an application package, not a library.");
        }

        renderer.Status("Checking", input);
        if (package is not null && package.Packages.Count > 1)
        {
            return Fail(renderer, "Run package dependencies with 'vela build' first, then execute the published native artifact.");
        }

        var compilation = Compile(input);
        renderer.PrintDiagnostics(compilation);
        if (compilation.HasErrors)
        {
            return 1;
        }

        renderer.Status("Running", Path.GetFileName(input));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "vela", Guid.NewGuid().ToString("N"));
        try
        {
            var buildService = new VelaBuildService(FindRuntimeProject());
            var generatedProject = buildService.WriteSourceProject(
                compilation,
                new BuildOptions(Path.GetFileNameWithoutExtension(input), temporaryDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(generatedProject);
            VelaConsoleRenderer.PrintProgramOutput(result);
            renderer.PrintProcessOutput(result, raw: options.Verbosity >= 2, includeStandardOutput: false, includeStandardError: false);
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

    private static async Task<int> BuildAsync(CommandOptions options, VelaConsoleRenderer renderer)
    {
        var input = ResolveInput(options, renderer, out var package);
        if (input is null)
        {
            return 2;
        }

        var target = options.Target ?? BuildTargetResolver.Auto;
        var mode = options.Mode ?? ExecutableMode.NativeAot;
        var isLibrary = options.BuildLibrary || package?.Root.Kind == VelaPackageKind.Library;
        if (isLibrary && mode != ExecutableMode.NativeAot)
        {
            return Fail(renderer, "Vela shared libraries require '--mode native-aot'.");
        }

        var resolvedTarget = BuildTargetResolver.Resolve(target);
        var outputDirectory = options.OutputDirectory ?? GetDefaultOutputDirectory(input, package, resolvedTarget.RuntimeIdentifier, isLibrary);
        renderer.Status("Resolving", package is null ? input : $"{package.Root.Name} v{package.Root.Version} ({package.Root.RootDirectory})");
        if (package is not null)
        {
            foreach (var dependency in package.Packages.Where(candidate => !ReferenceEquals(candidate, package.Root)))
            {
                renderer.Detail("Including", $"{dependency.Name} v{dependency.Version}");
            }
        }

        renderer.Status("Checking", input);
        var buildService = new VelaBuildService(FindRuntimeProject());
        var dependencyStagingDirectory = package is null
            ? null
            : Path.Combine(Path.GetTempPath(), "vela", "dependencies", Guid.NewGuid().ToString("N"));
        try
        {
            var imports = package is null
                ? []
                : await BuildDependenciesAsync(package, buildService, dependencyStagingDirectory!, target, renderer);
            if (isLibrary)
            {
                var packageName = package?.Root.Name ?? Path.GetFileNameWithoutExtension(input);
                var libraryCompilation = VelaCompiler.CompileLibrary(new SourceText(File.ReadAllText(input), input), packageName);
                renderer.PrintDiagnostics(libraryCompilation.Compilation);
                if (libraryCompilation.Compilation.HasErrors)
                {
                    return 1;
                }

                renderer.Status("Generating", $"native ABI exports for {packageName}");
                renderer.Status("Publishing", $"{resolvedTarget.RuntimeIdentifier} shared library");
                var result = await buildService.BuildLibraryAsync(
                    libraryCompilation,
                    new BuildOptions(packageName, outputDirectory, target, mode, VelaArtifactKind.SharedLibrary),
                    package?.Root.Version ?? "0.1.0");
                renderer.PrintProcessOutput(new ProcessResult(result.Build.Succeeded ? 0 : 1, result.Build.StandardOutput, result.Build.StandardError), raw: options.Verbosity >= 2);
                if (!result.Build.Succeeded)
                {
                    return 1;
                }

                renderer.Success("Finished", $"native library {result.Build.LibraryPath}");
                if (result.Manifest is not null)
                {
                    var manifestName = Path.GetFileNameWithoutExtension(result.Build.LibraryPath!) + ".velaabi.json";
                    renderer.Detail("Manifest", Path.Combine(Path.GetDirectoryName(result.Build.LibraryPath!)!, manifestName));
                }

                return 0;
            }

            var compilation = Compile(input, imports);
            renderer.PrintDiagnostics(compilation);
            if (compilation.HasErrors)
            {
                return 1;
            }

            renderer.Status("Lowering", "Vela semantic model to Native AOT C#");
            renderer.Status("Publishing", $"{resolvedTarget.RuntimeIdentifier} native executable");
            var build = await buildService.BuildAsync(
                compilation,
                new BuildOptions(Path.GetFileNameWithoutExtension(input), outputDirectory, target, mode));
            renderer.PrintProcessOutput(new ProcessResult(build.Succeeded ? 0 : 1, build.StandardOutput, build.StandardError), raw: options.Verbosity >= 2);
            if (!build.Succeeded)
            {
                return 1;
            }

            CopyDependencyArtifacts(imports, build.ExecutablePath!);

            renderer.Success("Finished", $"native executable {build.ExecutablePath}");
            renderer.Detail("Target", build.RuntimeIdentifier);
            renderer.Detail("Size", DescribeArtifactSize(build.ExecutablePath!));
            renderer.Detail("Bundle", DescribeBundleSize(Path.GetDirectoryName(build.ExecutablePath!)!));
            return 0;
        }
        finally
        {
            TryDeleteDirectory(dependencyStagingDirectory);
        }
    }

    private static int ShowTargets(CommandOptions options, VelaConsoleRenderer renderer)
    {
        if (options.InputPath is not null)
        {
            return Fail(renderer, "The targets command does not accept an input path.");
        }

        var target = BuildTargetResolver.Resolve(BuildTargetResolver.Auto);
        renderer.Status("Target", $"auto = {target.RuntimeIdentifier}");
        renderer.Detail("Usage", "vela build [path] --target <rid>");
        return 0;
    }

    private static string? ResolveInput(CommandOptions options, VelaConsoleRenderer renderer, out VelaPackageGraph? package)
    {
        package = null;
        var candidate = options.InputPath ?? Directory.GetCurrentDirectory();
        var fullPath = Path.GetFullPath(candidate);
        if (File.Exists(fullPath))
        {
            if (!string.Equals(Path.GetExtension(fullPath), ".vela", StringComparison.OrdinalIgnoreCase))
            {
                _ = Fail(renderer, "A source input must use the .vela extension.");
                return null;
            }

            return fullPath;
        }

        if (Directory.Exists(fullPath) || string.Equals(Path.GetFileName(fullPath), "vela.toml", StringComparison.OrdinalIgnoreCase))
        {
            package = VelaPackageResolver.Resolve(fullPath);
            return package.Root.EntryPointPath;
        }

        _ = Fail(renderer, $"Source file or Vela package not found: {fullPath}");
        return null;
    }

    private static string GetDefaultOutputDirectory(string input, VelaPackageGraph? package, string runtimeIdentifier, bool isLibrary)
    {
        var root = package?.Root.RootDirectory ?? Path.GetDirectoryName(input)!;
        var profile = "release";
        var name = isLibrary ? "lib" : "bin";
        return Path.Combine(root, "target", runtimeIdentifier, profile, name);
    }

    private static VelaCompilation Compile(string inputPath, IReadOnlyList<VelaLibraryImport>? imports = null) =>
        VelaCompiler.Compile(new SourceText(File.ReadAllText(inputPath), inputPath), imports ?? []);

    private static List<VelaLibraryImport> CreateCheckImports(VelaPackageGraph package, string runtimeIdentifier)
    {
        var imports = new List<VelaLibraryImport>();
        foreach (var dependency in package.Packages.Where(candidate => !ReferenceEquals(candidate, package.Root)))
        {
            if (dependency.Kind != VelaPackageKind.Library)
            {
                continue;
            }

            var source = new SourceText(File.ReadAllText(dependency.EntryPointPath), dependency.EntryPointPath);
            var compilation = VelaCompiler.CompileLibrary(source, dependency.Name);
            if (compilation.Compilation.HasErrors)
            {
                continue;
            }

            var libraryFileName = OperatingSystem.IsWindows()
                ? dependency.Name.Replace(".", "_", StringComparison.Ordinal) + ".dll"
                : "lib" + dependency.Name.Replace(".", "_", StringComparison.Ordinal) + ".so";
            var manifest = VelaAbiManifest.Create(dependency.Name, dependency.Version, runtimeIdentifier, libraryFileName, compilation.Exports);
            imports.Add(new VelaLibraryImport(dependency.Name, libraryFileName, manifest));
        }

        return imports;
    }

    private static async Task<IReadOnlyList<VelaLibraryImport>> BuildDependenciesAsync(
        VelaPackageGraph package,
        VelaBuildService buildService,
        string dependencyOutput,
        string target,
        VelaConsoleRenderer renderer)
    {
        var imports = new List<VelaLibraryImport>();
        foreach (var dependency in package.Packages.Where(candidate => !ReferenceEquals(candidate, package.Root)))
        {
            if (dependency.Kind != VelaPackageKind.Library)
            {
                throw new VelaPackageException($"Dependency '{dependency.Name}' is not a Vela library package.");
            }

            renderer.Status("Compiling", $"dependency {dependency.Name} v{dependency.Version}");
            var source = new SourceText(File.ReadAllText(dependency.EntryPointPath), dependency.EntryPointPath);
            var compilation = VelaCompiler.CompileLibrary(source, dependency.Name);
            renderer.PrintDiagnostics(compilation.Compilation);
            if (compilation.Compilation.HasErrors)
            {
                throw new VelaPackageException($"Dependency '{dependency.Name}' contains Vela compiler errors.");
            }

            var output = Path.Combine(dependencyOutput, dependency.Name.Replace(".", "_", StringComparison.Ordinal));
            var result = await buildService.BuildLibraryAsync(
                compilation,
                new BuildOptions(dependency.Name, output, target, ExecutableMode.NativeAot, VelaArtifactKind.SharedLibrary),
                dependency.Version);
            if (!result.Build.Succeeded || result.Build.LibraryPath is null || result.Manifest is null)
            {
                throw new VelaPackageException($"Failed to build native dependency '{dependency.Name}': {result.Build.StandardError}");
            }

            imports.Add(new VelaLibraryImport(dependency.Name, result.Build.LibraryPath, result.Manifest));
        }

        return imports;
    }

    private static void CopyDependencyArtifacts(IReadOnlyList<VelaLibraryImport> imports, string executablePath)
    {
        var outputDirectory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("The native executable has no parent directory.");
        foreach (var importItem in imports)
        {
            var sourceDirectory = Path.GetDirectoryName(importItem.LibraryPath) ?? throw new InvalidOperationException("A library dependency has no parent directory.");
            var libraryDestination = Path.Combine(outputDirectory, Path.GetFileName(importItem.LibraryPath));
            File.Copy(importItem.LibraryPath, libraryDestination, overwrite: true);
            foreach (var manifest in Directory.EnumerateFiles(sourceDirectory, "*.velaabi.json", SearchOption.TopDirectoryOnly))
            {
                File.Copy(manifest, Path.Combine(outputDirectory, Path.GetFileName(manifest)), overwrite: true);
            }
        }
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary dependency cleanup failure must not hide a successful build.
        }
        catch (UnauthorizedAccessException)
        {
            // A temporary dependency cleanup failure must not hide a successful build.
        }
    }

    private static string DescribeArtifactSize(string path)
    {
        var length = new FileInfo(path).Length;
        return $"{length:N0} bytes ({length / 1024d / 1024d:F2} MiB)";
    }

    private static string DescribeBundleSize(string directory)
    {
        const long threeMiB = 3 * 1024 * 1024;
        var length = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => !string.Equals(Path.GetExtension(path), ".velaabi.json", StringComparison.OrdinalIgnoreCase))
            .Sum(static path => new FileInfo(path).Length);
        var status = length <= threeMiB ? "within 3 MiB budget" : "over 3 MiB budget";
        return $"{length:N0} bytes ({length / 1024d / 1024d:F2} MiB; {status})";
    }

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

    private static int Fail(VelaConsoleRenderer renderer, string message)
    {
        renderer.Error("VEL9000", message);
        return 2;
    }

    private static void PrintUsage(VelaConsoleRenderer renderer)
    {
        renderer.Title("Vela compiler");
        renderer.Detail("check", "vela check [file.vela | package-directory]");
        renderer.Detail("run", "vela run [file.vela | package-directory]");
        renderer.Detail("build", "vela build [file.vela | package-directory] [--lib] [--output directory] [--target rid]");
        renderer.Detail("targets", "vela targets");
        renderer.Detail("output", "Detailed and colored by default; use -q or --quiet to reduce output.");
        renderer.Detail("color", "--color auto|always|never; use -vv to include raw .NET publishing output.");
    }
}

internal sealed record CommandOptions(
    string? Command,
    string? InputPath,
    string? OutputDirectory,
    string? Target,
    ExecutableMode? Mode,
    bool BuildLibrary,
    bool Quiet,
    int Verbosity,
    ColorMode ColorMode)
{
    public static CommandOptions Parse(string[] arguments)
    {
        string? command = null;
        string? input = null;
        string? output = null;
        string? target = null;
        ExecutableMode? mode = null;
        var buildLibrary = false;
        var quiet = false;
        var verbosity = 0;
        var color = ColorMode.Auto;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "--help" or "-h")
            {
                return new CommandOptions("help", null, null, null, null, false, false, 0, color);
            }

            if (argument is "--quiet" or "-q")
            {
                quiet = true;
                continue;
            }

            if (argument is "-v" or "--verbose")
            {
                verbosity++;
                continue;
            }

            if (argument == "-vv")
            {
                verbosity += 2;
                continue;
            }

            if (argument == "--lib")
            {
                buildLibrary = true;
                continue;
            }

            if (argument is "--output" or "--target" or "--mode" or "--color")
            {
                if (index + 1 >= arguments.Length)
                {
                    throw new ArgumentException($"Option '{argument}' requires a value.");
                }

                var value = arguments[++index];
                switch (argument)
                {
                    case "--output": output = Path.GetFullPath(value); break;
                    case "--target": target = value; break;
                    case "--mode": mode = ParseMode(value); break;
                    case "--color": color = ParseColorMode(value); break;
                }

                continue;
            }

            if (argument.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option '{argument}'.");
            }

            if (command is null)
            {
                command = argument;
            }
            else if (input is null)
            {
                input = argument;
            }
            else
            {
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            }
        }

        return new CommandOptions(command, input, output, target, mode, buildLibrary, quiet, verbosity, color);
    }

    private static ExecutableMode ParseMode(string value) => value switch
    {
        "native-aot" => ExecutableMode.NativeAot,
        "single-file" => ExecutableMode.SingleFile,
        "framework-dependent" => ExecutableMode.FrameworkDependent,
        _ => throw new ArgumentException("The build mode must be 'native-aot', 'single-file', or 'framework-dependent'.")
    };

    private static ColorMode ParseColorMode(string value) => value switch
    {
        "auto" => ColorMode.Auto,
        "always" => ColorMode.Always,
        "never" => ColorMode.Never,
        _ => throw new ArgumentException("The color mode must be 'auto', 'always', or 'never'.")
    };
}

internal enum ColorMode
{
    Auto,
    Always,
    Never
}

internal sealed class VelaConsoleRenderer
{
    private readonly IAnsiConsole _console;
    private readonly CommandOptions _options;

    public VelaConsoleRenderer(CommandOptions options)
    {
        _options = options;
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            // Spectre otherwise performs a second capability probe and can suppress
            // ANSI even when the user explicitly chose --color always.
            Ansi = options.ColorMode switch
            {
                ColorMode.Always => AnsiSupport.Yes,
                ColorMode.Never => AnsiSupport.No,
                _ => ShouldUseColor() ? AnsiSupport.Yes : AnsiSupport.No
            },
            ColorSystem = options.ColorMode switch
            {
                ColorMode.Always => ColorSystemSupport.TrueColor,
                ColorMode.Never => ColorSystemSupport.NoColors,
                _ => ShouldUseColor() ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors
            }
        });
    }

    public void Title(string title) => _console.MarkupLine($"[bold deepskyblue1]{Markup.Escape(title)}[/]");

    public void Status(string verb, string message)
    {
        if (!_options.Quiet)
        {
            _console.MarkupLine($"[bold {StatusColor(verb)}]{Markup.Escape(verb),12}[/] {Markup.Escape(message)}");
        }
    }

    public void Success(string verb, string message)
    {
        if (!_options.Quiet)
        {
            _console.MarkupLine($"[bold green]{Markup.Escape(verb),12}[/] [green]{Markup.Escape(message)}[/]");
        }
    }

    public void Detail(string label, string message)
    {
        if (!_options.Quiet)
        {
            _console.MarkupLine($"[dim {DetailColor(label)}]{Markup.Escape(label),12}[/] {Markup.Escape(message)}");
        }
    }

    public void Error(string code, string message) => _console.MarkupLine($"[bold red]error {Markup.Escape(code)}:[/] {Markup.Escape(message)}");

    public void PrintDiagnostics(VelaCompilation compilation)
    {
        foreach (var diagnostic in compilation.Diagnostics.OrderBy(static diagnostic => diagnostic.Span.Start))
        {
            var location = compilation.Source.GetLocation(diagnostic.Span);
            var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "red" : "yellow";
            _console.MarkupLine($"[bold {severity}]{diagnostic.Severity.ToString().ToLowerInvariant()} {Markup.Escape(diagnostic.Code)}:[/] {Markup.Escape(diagnostic.Message)} [dim]at {Markup.Escape(location.FilePath)}:{location.Line}:{location.Column}[/]");
            var sourceLine = compilation.Source.Text.Split(["\r\n", "\n"], StringSplitOptions.None).ElementAtOrDefault(location.Line - 1);
            if (sourceLine is not null)
            {
                _console.MarkupLine($"[dim]  |[/]");
                _console.MarkupLine($"[dim]{location.Line,2} |[/] {Highlight(sourceLine)}");
                _console.MarkupLine($"[dim]  |[/] [bold {severity}]{new string(' ', Math.Max(0, location.Column - 1))}^{new string('~', Math.Max(0, diagnostic.Span.Length - 1))}[/]");
            }

            if (!string.IsNullOrWhiteSpace(diagnostic.Help))
            {
                _console.MarkupLine($"[dim]  = help:[/] {Markup.Escape(diagnostic.Help)}");
            }
        }
    }

    public static void PrintProgramOutput(ProcessResult result)
    {
        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            Console.Out.Write(result.StandardOutput);
        }

        if (!string.IsNullOrEmpty(result.StandardError))
        {
            Console.Error.Write(result.StandardError);
        }
    }

    public void PrintProcessOutput(
        ProcessResult result,
        bool raw,
        bool includeStandardOutput = true,
        bool includeStandardError = true)
    {
        if (!raw)
        {
            return;
        }

        if (includeStandardOutput)
        {
            foreach (var line in SplitLines(result.StandardOutput))
            {
                _console.MarkupLine($"[dim]dotnet:[/] {Markup.Escape(line)}");
            }
        }

        if (!includeStandardError)
        {
            return;
        }

        foreach (var line in SplitLines(result.StandardError))
        {
            _console.MarkupLine($"[yellow]dotnet:[/] {Markup.Escape(line)}");
        }
    }

    private static string[] SplitLines(string value) => value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ShouldUseColor()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        var terminal = Environment.GetEnvironmentVariable("TERM");
        return !Console.IsOutputRedirected
            && string.IsNullOrEmpty(noColor)
            && !string.Equals(terminal, "dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static string StatusColor(string verb) => verb switch
    {
        "Resolving" or "Checking" => "deepskyblue1",
        "Compiling" or "Lowering" => "cornflowerblue",
        "Publishing" => "gold1",
        "Running" => "orchid",
        _ => "green"
    };

    private static string DetailColor(string label) => label switch
    {
        "Target" or "Size" or "Bundle" => "mediumorchid1",
        "Manifest" => "turquoise2",
        "Including" or "Resolving" => "deepskyblue1",
        _ => "grey70"
    };

    private static string Highlight(string source)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '#')
            {
                builder.Append("[green]").Append(Markup.Escape(source[index..])).Append("[/]");
                break;
            }

            if (source[index] == '"')
            {
                var end = index + 1;
                while (end < source.Length && source[end] != '"')
                {
                    end++;
                }

                end = Math.Min(source.Length, end + 1);
                builder.Append("[yellow]").Append(Markup.Escape(source[index..end])).Append("[/]");
                index = end;
                continue;
            }

            if (char.IsLetter(source[index]) || source[index] == '_')
            {
                var end = index + 1;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
                {
                    end++;
                }

                var token = source[index..end];
                var style = token is "fn" or "let" or "var" or "return" or "if" or "else" or "for" or "in" or "while" or "break" or "continue" or "switch" or "case" or "default" or "record" or "class" or "struct" or "interface" or "public" or "ffi" or "include" or "implements"
                    ? "blue"
                    : "white";
                builder.Append('[').Append(style).Append(']').Append(Markup.Escape(token)).Append("[/]");
                index = end;
                continue;
            }

            if (char.IsDigit(source[index]))
            {
                var end = index + 1;
                while (end < source.Length && (char.IsDigit(source[end]) || source[end] == '.'))
                {
                    end++;
                }

                builder.Append("[aqua]").Append(Markup.Escape(source[index..end])).Append("[/]");
                index = end;
                continue;
            }

            builder.Append(Markup.Escape(source[index].ToString()));
            index++;
        }

        return builder.ToString();
    }
}
