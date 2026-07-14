using System.Diagnostics;
using System.Security;

namespace Vela.Backend;

/// <summary>Writes generated source projects and invokes the local .NET publishing toolchain.</summary>
public sealed class VelaBuildService
{
    private readonly string _runtimeProjectPath;

    public VelaBuildService(string runtimeProjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeProjectPath);
        _runtimeProjectPath = Path.GetFullPath(runtimeProjectPath);
        if (!File.Exists(_runtimeProjectPath))
        {
            throw new FileNotFoundException("The Vela runtime project was not found.", _runtimeProjectPath);
        }
    }

    public async Task<BuildResult> BuildAsync(VelaCompilation compilation, BuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(options);
        if (compilation.HasErrors || compilation.GeneratedSource is null)
        {
            throw new InvalidOperationException("Cannot build source that contains compiler errors.");
        }

        var layout = WriteSourceProject(compilation, options);
        var arguments = new List<string>
        {
            "publish",
            layout.ProjectPath,
            "--configuration", "Release",
            "--runtime", options.RuntimeIdentifier,
            "--output", layout.PublishDirectory,
            "--nologo"
        };

        switch (options.Mode)
        {
            case ExecutableMode.NativeAot:
                arguments.AddRange(["--self-contained", "true", "-p:PublishAot=true", "-p:InvariantGlobalization=true"]);
                break;
            case ExecutableMode.SingleFile:
                arguments.AddRange(["--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true"]);
                break;
            case ExecutableMode.FrameworkDependent:
                arguments.Add("--self-contained");
                arguments.Add("false");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        var process = await RunDotnetAsync(arguments, layout.SourceDirectory, cancellationToken);
        return new BuildResult(
            process.ExitCode == 0,
            layout.SourceDirectory,
            layout.ProjectPath,
            layout.PublishDirectory,
            process.StandardOutput,
            process.StandardError);
    }

    public GeneratedProject WriteSourceProject(VelaCompilation compilation, BuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(options);
        if (compilation.HasErrors || compilation.GeneratedSource is null)
        {
            throw new InvalidOperationException("Cannot generate a project for source that contains compiler errors.");
        }

        var applicationName = SanitizeApplicationName(options.ApplicationName);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        var sourceDirectory = Path.Combine(outputDirectory, "source");
        var publishDirectory = Path.Combine(outputDirectory, "publish");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(publishDirectory);

        var sourcePath = Path.Combine(sourceDirectory, "Program.g.cs");
        var projectPath = Path.Combine(sourceDirectory, "Vela.Generated.csproj");
        File.WriteAllText(sourcePath, compilation.GeneratedSource);
        File.WriteAllText(projectPath, CreateProjectFile(applicationName));
        return new GeneratedProject(sourceDirectory, sourcePath, projectPath, publishDirectory);
    }

    public static async Task<ProcessResult> RunGeneratedProjectAsync(GeneratedProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return await RunDotnetAsync(["run", "--configuration", "Release", "--project", project.ProjectPath, "--nologo"], project.SourceDirectory, cancellationToken);
    }

    private string CreateProjectFile(string applicationName)
    {
        var escapedRuntimePath = SecurityElement.Escape(_runtimeProjectPath) ?? throw new InvalidOperationException("Unable to escape the runtime project path.");
        var escapedApplicationName = SecurityElement.Escape(applicationName) ?? throw new InvalidOperationException("Unable to escape the application name.");

        return $"""
               <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup>
                   <OutputType>Exe</OutputType>
                   <TargetFramework>net10.0</TargetFramework>
                   <AssemblyName>{escapedApplicationName}</AssemblyName>
                   <Nullable>enable</Nullable>
                   <ImplicitUsings>enable</ImplicitUsings>
                   <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                   <IsAotCompatible>true</IsAotCompatible>
                 </PropertyGroup>
                 <ItemGroup>
                   <ProjectReference Include="{escapedRuntimePath}" />
                 </ItemGroup>
               </Project>
               """;
    }

    private static async Task<ProcessResult> RunDotnetAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the .NET SDK process.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await standardOutputTask, await standardErrorTask);
    }

    private static string SanitizeApplicationName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sanitized = new string(name.Select(static character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "VelaApplication" : sanitized;
    }
}

/// <summary>Paths of generated project files prepared for run or publish.</summary>
public sealed record GeneratedProject(string SourceDirectory, string SourcePath, string ProjectPath, string PublishDirectory);

/// <summary>Captured result from a local .NET SDK command.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
