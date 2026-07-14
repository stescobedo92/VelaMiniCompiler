using Vela.Backend;
using Vela.Core.Source;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaBackendTests
{
    [Fact]
    public async Task CompileBasicProgramGeneratesAndRunsFrameworkDependentProject()
    {
        const string code = """
            fn main() -> Int:
                print("basic-ready")
                return 0
            """;
        var compilation = Compile(code, "basic.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("private static long main()", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(\"basic-ready\")", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());
            var project = service.WriteSourceProject(
                compilation,
                new BuildOptions("BasicProgram", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("basic-ready", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileGenericProgramGeneratesAndRunsFrameworkDependentProject()
    {
        const string code = """
            record Box<T>:
                value: T

            fn identity<T>(value: T) -> T:
                return value

            fn main() -> Int:
                let box: Box<Int> = Box<Int>(identity<Int>(41))
                print(box.value + 1)
                return 0
            """;
        var compilation = Compile(code, "generic.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("public sealed record Box<T>(T value);", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("private static T identity<T>(T value)", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("new Box<long>(identity<long>(41L))", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());
            var project = service.WriteSourceProject(
                compilation,
                new BuildOptions("GenericProgram", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("42", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileImmutableAssignmentReportsDiagnostic()
    {
        const string code = """
            fn main() -> Int:
                let value: Int = 1
                value = 2
                return value
            """;

        var compilation = Compile(code, "immutable-assignment.vela");

        Assert.True(compilation.HasErrors);
        Assert.Null(compilation.GeneratedSource);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3004");
        Assert.Contains("immutable binding 'value'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileTypeMismatchReportsDiagnostic()
    {
        const string code = """
            fn main() -> Int:
                let value: Int = "wrong"
                return 0
            """;

        var compilation = Compile(code, "type-mismatch.vela");

        Assert.True(compilation.HasErrors);
        Assert.Null(compilation.GeneratedSource);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3002");
        Assert.Equal("Type mismatch: expected Int, found Text.", diagnostic.Message);
    }

    [Fact]
    public void CompileAssertionEmitsManagedContractCheck()
    {
        const string code = "fn main() -> Int:\n    assert 2 > 0, \"positive\"\n    return 0\n";

        var compilation = Compile(code, "assertion.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("Contract.Require(2L > 0L, \"positive\");", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteSourceProjectCreatesExpectedFrameworkDependentLayout()
    {
        const string code = """
            fn main() -> Int:
                return 0
            """;
        var compilation = Compile(code, "layout.vela");
        Assert.False(compilation.HasErrors);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var options = new BuildOptions("Layout App", outputDirectory, "win-x64", ExecutableMode.FrameworkDependent);
            var service = new VelaBuildService(GetRuntimeProjectPath());

            var project = service.WriteSourceProject(compilation, options);

            Assert.Equal(Path.Combine(Path.GetFullPath(outputDirectory), "source"), project.SourceDirectory);
            Assert.Equal(Path.Combine(project.SourceDirectory, "Program.g.cs"), project.SourcePath);
            Assert.Equal(Path.Combine(project.SourceDirectory, "Vela.Generated.csproj"), project.ProjectPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(outputDirectory), "publish"), project.PublishDirectory);
            Assert.True(File.Exists(project.SourcePath));
            Assert.True(File.Exists(project.ProjectPath));
            Assert.True(Directory.Exists(project.PublishDirectory));
            Assert.Equal(compilation.GeneratedSource, File.ReadAllText(project.SourcePath));

            var projectFile = File.ReadAllText(project.ProjectPath);
            Assert.Contains("<AssemblyName>Layout_App</AssemblyName>", projectFile, StringComparison.Ordinal);
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", projectFile, StringComparison.Ordinal);
            Assert.Contains("Vela.Runtime.csproj", projectFile, StringComparison.Ordinal);
            Assert.DoesNotContain("PublishAot", projectFile, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void WriteSourceProjectSanitizesTheGeneratedAssemblyName()
    {
        var compilation = Compile("fn main() -> Int:\n    return 0\n", "safe-name.vela");
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());
            var project = service.WriteSourceProject(
                compilation,
                new BuildOptions("name <with>/unsafe characters", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var projectFile = File.ReadAllText(project.ProjectPath);
            Assert.Contains("<AssemblyName>name__with__unsafe_characters</AssemblyName>", projectFile, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    private static VelaCompilation Compile(string code, string filePath) =>
        VelaCompiler.Compile(new SourceText(code, filePath));

    private static string GetRuntimeProjectPath() =>
        Path.Combine(FindRepositoryRoot(), "src", "Vela.Runtime", "Vela.Runtime.csproj");

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vela-backend-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var runtimeProject = Path.Combine(directory.FullName, "src", "Vela.Runtime", "Vela.Runtime.csproj");
            if (File.Exists(runtimeProject))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Vela repository root.");
    }
}
