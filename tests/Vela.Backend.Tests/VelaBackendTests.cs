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

    [Fact]
    public void ResolveAutoTargetUsesTheCurrentRuntimeIdentifier()
    {
        var target = BuildTargetResolver.Resolve(BuildTargetResolver.Auto);

        Assert.True(target.IsHostTarget);
        Assert.False(string.IsNullOrWhiteSpace(target.RuntimeIdentifier));
    }

    [Fact]
    public void ResolveExplicitTargetPreservesTheRequestedRuntimeIdentifier()
    {
        var target = BuildTargetResolver.Resolve("  linux-x64  ");

        Assert.False(target.IsHostTarget);
        Assert.Equal("linux-x64", target.RuntimeIdentifier);
    }

    [Fact]
    public void FindPrimaryExecutablePrefersTheWindowsExecutableWhenBothCandidatesExist()
    {
        var publishDirectory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(publishDirectory, "sample.exe");
            File.WriteAllText(executable, string.Empty);
            File.WriteAllText(Path.Combine(publishDirectory, "sample"), string.Empty);

            Assert.Equal(executable, VelaBuildService.FindPrimaryExecutable(publishDirectory, "sample"));
        }
        finally
        {
            DeleteTemporaryDirectory(publishDirectory);
        }
    }

    [Fact]
    public async Task BuildAsyncPublishesTheHostExecutableIntoTheRequestedDirectory()
    {
        var compilation = Compile("fn main() -> Int:\n    print(\"published-ready\")\n    return 0\n", "published.vela");
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());

            var result = await service.BuildAsync(
                compilation,
                new BuildOptions("PublishedProgram", outputDirectory, BuildTargetResolver.Auto, ExecutableMode.FrameworkDependent));

            Assert.True(result.Succeeded, result.StandardError);
            Assert.NotNull(result.ExecutablePath);
            Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(result.ExecutablePath));
            Assert.True(File.Exists(result.ExecutablePath));
            Assert.False(Directory.Exists(Path.Combine(outputDirectory, "source")));
            Assert.False(Directory.Exists(Path.Combine(outputDirectory, "publish")));
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileCollectionsGeneratesAndRunsNativeRuntimeCalls()
    {
        const string code = """
            fn main() -> Int:
                var values = Vector<Int>(4)
                values.append(7)
                values.append(11)
                values[0] = 9

                var scores = HashMap<Text, Int>(4)
                scores.set("Ada", 42)
                scores["Grace"] = 36

                var ids = HashSet<Int>()
                print(ids.add(1))

                var queue = Queue<Int>()
                queue.enqueue(5)
                print(queue.dequeue().value)

                var stack = Stack<Int>()
                stack.push(8)
                print(stack.pop().value)

                var ring = RingBuffer<Int>(2)
                print(ring.try_enqueue(3))

                var bits = BitSet(8)
                bits.set(3)
                print(bits.contains(3))

                for value in values:
                    print(value)

                return values.count
            """;
        var compilation = Compile(code, "collections.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("new VelaVector<long>(checked((int)4L))", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("new VelaHashMap<string, long>(checked((int)4L))", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("foreach (var value in values)", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("scores[\"Grace\"] = 36L", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());
            var project = service.WriteSourceProject(
                compilation,
                new BuildOptions("CollectionProgram", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("True", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("5", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("8", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("9", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("11", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileCollectionWithMutableHashKeyReportsDiagnostic()
    {
        const string code = "fn main() -> Int:\n    var map = HashMap<Vector<Int>, Int>()\n    return 0\n";

        var compilation = Compile(code, "invalid-hash-key.vela");

        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3006");
        Assert.Contains("cannot be used as a hash key", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileUnsupportedCollectionIterationReportsDiagnostic()
    {
        const string code = "fn main() -> Int:\n    var bits = BitSet(8)\n    for bit in bits:\n        print(bit)\n    return 0\n";

        var compilation = Compile(code, "invalid-iteration.vela");

        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3006");
        Assert.Contains("cannot be iterated", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Vector<Int>(-1)", "capacity cannot be negative")]
    [InlineData("RingBuffer<Int>(0)", "capacity must be positive")]
    public void CompileInvalidCollectionCapacityReportsDiagnostic(string construction, string expectedMessage)
    {
        var code = $"fn main() -> Int:\n    var values = {construction}\n    return 0\n";

        var compilation = Compile(code, "invalid-capacity.vela");

        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3006");
        Assert.Contains(expectedMessage, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileIterableCollectionsGeneratesForeachForEachSupportedType()
    {
        const string code = """
            fn main() -> Int:
                var ids = HashSet<Int>()
                ids.add(1)
                var queue = Queue<Int>()
                queue.enqueue(2)
                var stack = Stack<Int>()
                stack.push(3)
                var ring = RingBuffer<Int>(1)
                ring.try_enqueue(4)

                for value in ids:
                    print(value)
                for value in queue:
                    print(value)
                for value in stack:
                    print(value)
                for value in ring:
                    print(value)
                return 0
            """;
        var compilation = Compile(code, "iterable-collections.vela");

        Assert.False(compilation.HasErrors);
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new VelaBuildService(GetRuntimeProjectPath());
            var project = service.WriteSourceProject(
                compilation,
                new BuildOptions("IterableCollections", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("1", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("2", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("3", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("4", result.StandardOutput, StringComparison.Ordinal);
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
