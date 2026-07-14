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

        var target = BuildTargetResolver.Resolve(options.RuntimeIdentifier);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "vela", "publish", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var layout = WriteSourceProject(compilation, options with { OutputDirectory = stagingDirectory, RuntimeIdentifier = target.RuntimeIdentifier });
            var arguments = CreatePublishArguments(layout, target.RuntimeIdentifier, options.Mode, options.ArtifactKind);
            var process = await RunDotnetAsync(arguments, layout.SourceDirectory, cancellationToken);
            if (process.ExitCode != 0)
            {
                return new BuildResult(false, target.RuntimeIdentifier, null, process.StandardOutput, process.StandardError);
            }

            var applicationName = SanitizeApplicationName(options.ApplicationName);
            var primaryArtifact = options.ArtifactKind == VelaArtifactKind.Application
                ? FindPrimaryExecutable(layout.PublishDirectory, applicationName)
                : FindPrimaryLibrary(layout.PublishDirectory, applicationName);
            if (primaryArtifact is null)
            {
                var kind = options.ArtifactKind == VelaArtifactKind.Application ? "executable" : "shared library";
                var message = $"The publish output did not contain the expected {kind} for '{applicationName}'.";
                return new BuildResult(false, target.RuntimeIdentifier, null, process.StandardOutput, AppendError(process.StandardError, message));
            }

            var finalArtifact = CommitPublishedArtifacts(layout.PublishDirectory, primaryArtifact, options.OutputDirectory);
            return options.ArtifactKind == VelaArtifactKind.Application
                ? new BuildResult(true, target.RuntimeIdentifier, finalArtifact, process.StandardOutput, process.StandardError)
                : new BuildResult(true, target.RuntimeIdentifier, null, process.StandardOutput, process.StandardError, finalArtifact);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    /// <summary>Publishes a Vela library and writes its ABI manifest beside the native artifact.</summary>
    public async Task<VelaLibraryBuildResult> BuildLibraryAsync(
        VelaLibraryCompilation compilation,
        BuildOptions options,
        string packageVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        if (compilation.Compilation.HasErrors || compilation.Compilation.GeneratedSource is null)
        {
            throw new InvalidOperationException("Cannot build a library source that contains compiler errors.");
        }

        var build = await BuildAsync(
            compilation.Compilation,
            options with { ArtifactKind = VelaArtifactKind.SharedLibrary, Mode = ExecutableMode.NativeAot },
            cancellationToken);
        if (!build.Succeeded || build.LibraryPath is null)
        {
            return new VelaLibraryBuildResult(build, null);
        }

        var manifest = VelaAbiManifest.Create(
            options.ApplicationName,
            packageVersion,
            build.RuntimeIdentifier,
            Path.GetFileName(build.LibraryPath),
            compilation.Exports);
        var manifestPath = Path.Combine(Path.GetDirectoryName(build.LibraryPath)!, $"{SanitizeApplicationName(options.ApplicationName)}.velaabi.json");
        manifest.Write(manifestPath);
        return new VelaLibraryBuildResult(build, manifest);
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
        File.WriteAllText(projectPath, CreateProjectFile(applicationName, options.ArtifactKind));
        return new GeneratedProject(sourceDirectory, sourcePath, projectPath, publishDirectory);
    }

    public static async Task<ProcessResult> RunGeneratedProjectAsync(GeneratedProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return await RunDotnetAsync(["run", "--configuration", "Release", "--project", project.ProjectPath, "--nologo"], project.SourceDirectory, cancellationToken);
    }

    /// <summary>Finds the executable produced for an application without inferring an extension from a RID.</summary>
    public static string? FindPrimaryExecutable(string publishDirectory, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var sanitizedName = SanitizeApplicationName(applicationName);
        foreach (var candidate in new[]
        {
            Path.Combine(publishDirectory, sanitizedName + ".exe"),
            Path.Combine(publishDirectory, sanitizedName)
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Finds the platform-native library output without parsing a runtime identifier.</summary>
    public static string? FindPrimaryLibrary(string publishDirectory, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var sanitizedName = SanitizeApplicationName(applicationName);
        foreach (var candidate in new[]
        {
            Path.Combine(publishDirectory, sanitizedName + ".dll"),
            Path.Combine(publishDirectory, "lib" + sanitizedName + ".so"),
            Path.Combine(publishDirectory, sanitizedName + ".so"),
            Path.Combine(publishDirectory, "lib" + sanitizedName + ".dylib"),
            Path.Combine(publishDirectory, sanitizedName + ".dylib")
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string CreateProjectFile(string applicationName, VelaArtifactKind artifactKind)
    {
        var escapedRuntimePath = SecurityElement.Escape(_runtimeProjectPath) ?? throw new InvalidOperationException("Unable to escape the runtime project path.");
        var escapedApplicationName = SecurityElement.Escape(applicationName) ?? throw new InvalidOperationException("Unable to escape the application name.");
        var outputType = artifactKind == VelaArtifactKind.SharedLibrary ? "Library" : "Exe";
        var nativeLibraryProperty = artifactKind == VelaArtifactKind.SharedLibrary
            ? "                   <NativeLib>Shared</NativeLib>" + Environment.NewLine
            : string.Empty;

        return $"""
               <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup>
                   <OutputType>{outputType}</OutputType>
                   <TargetFramework>net10.0</TargetFramework>
                   <AssemblyName>{escapedApplicationName}</AssemblyName>
                   <Nullable>enable</Nullable>
                   <ImplicitUsings>enable</ImplicitUsings>
                   <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                   <IsAotCompatible>true</IsAotCompatible>
                   <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
               {nativeLibraryProperty}
                 </PropertyGroup>
                 <ItemGroup>
                   <ProjectReference Include="{escapedRuntimePath}" />
                 </ItemGroup>
               </Project>
               """;
    }

    private static List<string> CreatePublishArguments(GeneratedProject layout, string runtimeIdentifier, ExecutableMode mode, VelaArtifactKind artifactKind)
    {
        var arguments = new List<string>
        {
            "publish",
            layout.ProjectPath,
            "--configuration", "Release",
            "--runtime", runtimeIdentifier,
            "--output", layout.PublishDirectory,
            "--nologo"
        };

        if (artifactKind == VelaArtifactKind.SharedLibrary)
        {
            if (mode != ExecutableMode.NativeAot)
            {
                throw new InvalidOperationException("Vela shared libraries require the Native AOT publishing mode.");
            }

            arguments.AddRange(["--self-contained", "true", "-p:PublishAot=true", "-p:NativeLib=Shared", "-p:InvariantGlobalization=true"]);
            return arguments;
        }

        switch (mode)
        {
            case ExecutableMode.NativeAot:
                arguments.AddRange(["--self-contained", "true", "-p:PublishAot=true", "-p:InvariantGlobalization=true"]);
                break;
            case ExecutableMode.SingleFile:
                arguments.AddRange(["--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true"]);
                break;
            case ExecutableMode.FrameworkDependent:
                arguments.AddRange(["--self-contained", "false"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return arguments;
    }

    private static string CommitPublishedArtifacts(string publishDirectory, string executablePath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var normalizedPublishDirectory = Path.GetFullPath(publishDirectory);
        var normalizedOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(normalizedOutputDirectory);

        var primaryRelativePath = GetSafeRelativePath(normalizedPublishDirectory, executablePath);
        var stagedFiles = Directory.EnumerateFiles(normalizedPublishDirectory, "*", SearchOption.AllDirectories)
            .Where(static file => IsRuntimeArtifact(file))
            .Select(file => new PublishedFile(file, GetSafeRelativePath(normalizedPublishDirectory, file)))
            .ToArray();

        var temporaryFiles = new List<StagedArtifact>(stagedFiles.Length);
        try
        {
            foreach (var file in stagedFiles)
            {
                var destination = Path.Combine(normalizedOutputDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? normalizedOutputDirectory);
                var temporaryPath = destination + ".vela-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(file.SourcePath, temporaryPath, overwrite: false);
                CopyUnixFileMode(file.SourcePath, temporaryPath);
                temporaryFiles.Add(new StagedArtifact(temporaryPath, destination, string.Equals(file.RelativePath, primaryRelativePath, StringComparison.Ordinal)));
            }

            foreach (var artifact in temporaryFiles.Where(static artifact => !artifact.IsPrimary))
            {
                File.Move(artifact.TemporaryPath, artifact.DestinationPath, overwrite: true);
            }

            var primary = temporaryFiles.Single(static artifact => artifact.IsPrimary);
            File.Move(primary.TemporaryPath, primary.DestinationPath, overwrite: true);
            return primary.DestinationPath;
        }
        finally
        {
            foreach (var artifact in temporaryFiles)
            {
                if (File.Exists(artifact.TemporaryPath))
                {
                    File.Delete(artifact.TemporaryPath);
                }
            }
        }
    }

    private static string GetSafeRelativePath(string rootDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, path);
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relativePath == "..")
        {
            throw new InvalidOperationException("Publish output contains a path outside its staging directory.");
        }

        return relativePath;
    }

    private static bool IsRuntimeArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendError(string standardError, string message) => string.IsNullOrWhiteSpace(standardError)
        ? message + Environment.NewLine
        : standardError + Environment.NewLine + message + Environment.NewLine;

    private static void CopyUnixFileMode(string sourcePath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A cleanup failure must not hide the compiler result.
        }
        catch (UnauthorizedAccessException)
        {
            // A cleanup failure must not hide the compiler result.
        }
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

    private sealed record PublishedFile(string SourcePath, string RelativePath);

    private sealed record StagedArtifact(string TemporaryPath, string DestinationPath, bool IsPrimary);
}

/// <summary>Paths of generated project files prepared for run or publish.</summary>
public sealed record GeneratedProject(string SourceDirectory, string SourcePath, string ProjectPath, string PublishDirectory);

/// <summary>Captured result from a local .NET SDK command.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
