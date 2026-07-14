using System.Text.Json;
using Vela.Backend;
using Vela.Core.Source;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaBackendTests
{
    [Fact]
    public async Task CompileBasicProgramGeneratesAndRunsFrameworkDependentProject()
    {
        var compilation = Compile("""
            include vela.core;
            fn main() -> Int {
                print("basic-ready");
                return 0;
            }
            """, "basic.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("internal static int main()", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(\"basic-ready\")", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
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
    public async Task CompileObjectNumericAndSafetyFeaturesRuns()
    {
        var compilation = Compile("""
            include vela.core;

            interface Printable {
                fn render() -> Text;
            }

            struct Point {
                x: Int;
                y: Int;
            }

            class Counter implements Printable {
                var value: Int;
                fn increment() -> Int {
                    self.value = self.value + 1;
                    return self.value;
                }
                fn render() -> Text {
                    return "Counter";
                }
            }

            fn main() -> Int {
                let point = Point(3, 4);
                let counter = Counter(point.x);
                let boxed: Any = counter.increment();
                let value: Int = unbox<Int>(boxed);
                var values = Array<Int>(2);
                values[0] = value;
                values[1] = Int(7);
                let total: Long = Long(values[0]) + Long(values[1]);
                print(counter.render());
                print(Double(Decimal(total)));
                return 0;
            }
            """, "objects.vela");

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.GeneratedSource);
        Assert.Contains("class Counter", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaAny.Unbox<int>", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaNumeric.Add", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("ObjectProgram", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Counter", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("11", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileImmutableAssignmentReportsDiagnostic()
    {
        var compilation = Compile("""
            fn main() -> Int {
                let value: Int = 1;
                value = 2;
                return value;
            }
            """, "immutable-assignment.vela");

        Assert.True(compilation.HasErrors);
        Assert.Null(compilation.GeneratedSource);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "VEL3004");
        Assert.Contains("immutable binding 'value'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let value: Int = \"wrong\";", "Type mismatch: expected Int, found Text.")]
    [InlineData("let value: Int = 2147483648;", "outside the Int range")]
    public void CompileInvalidTypeOrLiteralReportsDiagnostic(string statement, string expectedMessage)
    {
        var compilation = Compile($"fn main() -> Int {{\n    {statement}\n    return 0;\n}}", "invalid.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, item => item.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void CompileAssertionEmitsManagedContractCheck()
    {
        var compilation = Compile("fn main() -> Int { assert 2 > 0, \"positive\"; return 0; }", "assertion.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("Contract.Require(2 > 0, \"positive\");", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileCollectionAndLoopEmitsSafeRuntimeOperations()
    {
        var compilation = Compile("""
            fn main() -> Int {
                var values = Vector<Int>(4);
                values.append(7);
                values[0] = 9;
                for value in values {
                    print(value);
                }
                return values.count;
            }
            """, "collections.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("new VelaVector<int>", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("values.Set(0", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("foreach (var value in values)", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HashMap<Vector<Int>, Int>()", "cannot be used as a hash key")]
    [InlineData("Vector<Int>(-1)", "capacity cannot be negative")]
    [InlineData("RingBuffer<Int>(0)", "capacity must be positive")]
    public void CompileInvalidCollectionConstructionReportsDiagnostic(string construction, string expectedMessage)
    {
        var compilation = Compile($"fn main() -> Int {{ var values = {construction}; return 0; }}", "invalid-collection.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, item => item.Code == "VEL3006" && item.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void CompileLibraryProducesStableScalarAbiManifest()
    {
        var library = VelaCompiler.CompileLibrary(new SourceText("""
            include vela.core;
            public ffi fn add(left: Int, right: Int) -> Int {
                return left + right;
            }
            """, "lib.vela"), "vela.math");

        Assert.False(library.Compilation.HasErrors);
        var exportItem = Assert.Single(library.Exports);
        Assert.Equal("add", exportItem.Name);
        Assert.Equal("vela_vela_math_add", exportItem.Symbol);
        Assert.Equal(["Int", "Int"], exportItem.Parameters);
        Assert.Equal("Int", exportItem.ReturnType);

        var manifest = VelaAbiManifest.Create("vela.math", "0.1.0", "win-x64", "vela_math.dll", library.Exports);
        var duplicate = VelaAbiManifest.Create("vela.math", "0.1.0", "win-x64", "vela_math.dll", library.Exports);
        Assert.Equal(manifest.ContractHash, duplicate.ContractHash);
        Assert.Contains("UnmanagedCallersOnly", library.Compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileConsumerBindsDeclaredLibraryImport()
    {
        var manifest = VelaAbiManifest.Create(
            "vela.math",
            "0.1.0",
            "win-x64",
            "vela_math.dll",
            [new VelaFfiExport("add", "vela_vela_math_add", ["Int", "Int"], "Int")]);
        var compilation = VelaCompiler.Compile(
            new SourceText("include vela.math as math; fn main() -> Int { return math.add(40, 2); }", "app.vela"),
            [new VelaLibraryImport("vela.math", "vela_math.dll", manifest)]);

        Assert.False(compilation.HasErrors);
        Assert.Contains("LibraryImport(\"vela_math\"", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaImports.Import_math_add(40, 2)", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryImportPreservesWindowsDllNameAndNormalizesUnixLibPrefix()
    {
        var windowsManifest = VelaAbiManifest.Create("liberty", "0.1.0", "win-x64", "liberty.dll", []);
        var unixManifest = VelaAbiManifest.Create("vela.math", "0.1.0", "linux-x64", "libvela_math.so", []);

        Assert.Equal("liberty", new VelaLibraryImport("liberty", "liberty.dll", windowsManifest).LibraryName);
        Assert.Equal("vela_math", new VelaLibraryImport("vela.math", "libvela_math.so", unixManifest).LibraryName);
    }

    [Fact]
    public void PackageResolverWritesDeterministicLockFileInDependencyOrder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var library = Path.Combine(root, "library");
            Directory.CreateDirectory(Path.Combine(library, "src"));
            File.WriteAllText(Path.Combine(library, "vela.toml"), "[package]\nname = \"vela.math\"\nversion = \"0.1.0\"\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(library, "src", "lib.vela"), "public ffi fn add(left: Int, right: Int) -> Int { return left + right; }");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "vela.toml"), "[package]\nname = \"vela.app\"\nversion = \"0.1.0\"\nkind = \"application\"\n\n[dependencies]\nvela.math = { path = \"library\" }\n");
            File.WriteAllText(Path.Combine(root, "src", "main.vela"), "include vela.math as math; fn main() -> Int { return math.add(40, 2); }");

            var graph = VelaPackageResolver.Resolve(root);
            var second = VelaPackageResolver.Resolve(root);

            Assert.Equal(["vela.math", "vela.app"], graph.BuildOrder.Select(static item => item.Name));
            Assert.Equal(graph.Packages.Select(static item => item.Name), second.Packages.Select(static item => item.Name));
            var lockFile = File.ReadAllText(graph.LockFilePath);
            using var document = JsonDocument.Parse(lockFile);
            Assert.Equal(1, document.RootElement.GetProperty("LockVersion").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("Packages").GetArrayLength());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void PackageResolverRejectsDependencyCycles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            CreateLibraryPackage(first, "first", "../second");
            CreateLibraryPackage(second, "second", "../first");

            var exception = Assert.Throws<VelaPackageException>(() => VelaPackageResolver.Resolve(first));
            Assert.Contains("Dependency cycle", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void WriteSourceProjectCreatesExpectedFrameworkDependentLayout()
    {
        var compilation = Compile("fn main() -> Int { return 0; }", "layout.vela");
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("Layout App", outputDirectory, "win-x64", ExecutableMode.FrameworkDependent));

            Assert.Equal(Path.Combine(Path.GetFullPath(outputDirectory), "source"), project.SourceDirectory);
            Assert.True(File.Exists(project.SourcePath));
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
    public void ResolveTargetAndArtifactDiscoveryAreHostAware()
    {
        var target = BuildTargetResolver.Resolve(BuildTargetResolver.Auto);
        Assert.True(target.IsHostTarget);
        Assert.False(string.IsNullOrWhiteSpace(target.RuntimeIdentifier));

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

    private static VelaCompilation Compile(string code, string filePath) =>
        VelaCompiler.Compile(new SourceText(code, filePath));

    private static void CreateLibraryPackage(string directory, string name, string dependencyPath)
    {
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "vela.toml"), $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\nkind = \"library\"\n\n[dependencies]\n{(name == "first" ? "second" : "first")} = {{ path = \"{dependencyPath}\" }}\n");
        File.WriteAllText(Path.Combine(directory, "src", "lib.vela"), "public ffi fn value() -> Int { return 1; }");
    }

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
            if (File.Exists(Path.Combine(directory.FullName, "src", "Vela.Runtime", "Vela.Runtime.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Vela repository root.");
    }
}
