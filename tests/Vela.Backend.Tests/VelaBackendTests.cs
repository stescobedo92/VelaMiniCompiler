using System.Text.Json;
using Vela.Backend;
using Vela.Core.Diagnostics;
using Vela.Core.Source;
using Xunit;

namespace Vela.Backend.Tests;

public sealed class VelaBackendTests
{
    private static readonly string[] CliLogPackageNames = ["vela.std.cli", "vela.std.log"];

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

            class Counter(start: Int) implements Printable {
                var value: Int = start;
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
    public void PackageResolverAcceptsSourceLibraryAndKeepsItsCanonicalLockKind()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sourceLibrary = Path.Combine(root, "config");
            Directory.CreateDirectory(Path.Combine(sourceLibrary, "src"));
            File.WriteAllText(Path.Combine(sourceLibrary, "vela.toml"), "[package]\nname = \"vela.std.config\"\nversion = \"0.1.0\"\nkind = \"source-library\"\n");
            File.WriteAllText(Path.Combine(sourceLibrary, "src", "lib.vela"), "fn config_name() -> Text { return \"vela\"; }");

            var graph = VelaPackageResolver.Resolve(sourceLibrary);

            Assert.Equal(VelaPackageKind.SourceLibrary, graph.Root.Kind);
            Assert.EndsWith(Path.Combine("src", "lib.vela"), graph.Root.EntryPointPath, StringComparison.Ordinal);
            var lockFile = File.ReadAllText(graph.LockFilePath);
            Assert.Contains("\"source-library\"", lockFile, StringComparison.Ordinal);
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

    [Fact]
    public async Task CompileCoreModulesAndControlFlowGeneratesAndRuns()
    {
        var compilation = Compile("""
            include vela.core;
            include vela.core.json;
            include vela.core.crypto;
            include vela.core.text;
            include vela.core.math;

            fn main() -> Int {
                let payload: Text = "{ \"id\": 42 }";
                var value: Int = 0;
                while value < 5 {
                    value = value + 1;
                    if value == 2 {
                        continue;
                    }
                    if value == 4 {
                        break;
                    }
                }
                switch value {
                    case 4 {
                        print(json.try_get_int(payload, "id").value);
                    }
                    default {
                        print("unexpected");
                    }
                }
                print(text.slice(crypto.sha256("vela"), 0, 8));
                print(math.sqrt(81));
                print(math.min(5, 2));
                print(math.clamp(15, 0, 10));
                return 0;
            }
            """, "core-control.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("while (", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaJson.TryGetInt", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaCrypto.Sha256", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("CoreControl", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("42", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("9", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("2", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("10", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileInvalidLoopControlAndSwitchCasesReportsDiagnostics()
    {
        var compilation = Compile("""
            fn main() -> Int {
                break;
                switch 1 {
                    case 1 { print("one"); }
                    case 1 { print("duplicate"); }
                }
                return 0;
            }
            """, "invalid-control.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VEL3015");
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Message.Contains("Duplicate switch case", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileExhaustiveEnumSwitchGeneratesNativeEnumAndRuns()
    {
        var compilation = Compile("""
            enum State { Ready, Running, Failed }

            fn main() -> Int {
                let state: State = State.Running;
                switch state {
                    case State.Ready { return 1; }
                    case State.Running { return 0; }
                    case State.Failed { return 2; }
                }
            }
            """, "enum-switch.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("enum State", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("Unreachable exhaustive Vela enum switch", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("EnumSwitch", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileNonExhaustiveEnumSwitchReportsMissingMembers()
    {
        var compilation = Compile("""
            enum State { Ready, Running }
            fn main() -> Int {
                let state: State = State.Ready;
                switch state {
                    case State.Ready { return 0; }
                }
            }
            """, "enum-missing.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VEL3016");
    }

    [Fact]
    public async Task CompileDeferSnapshotsArgumentsAndExecutesInLastInFirstOutOrder()
    {
        var compilation = Compile("""
            fn mark(value: Int) -> Void {
                print(value);
            }

            fn main() -> Int {
                var value: Int = 0;
                defer mark(value);
                value = 1;
                defer mark(2);
                return 0;
            }
            """, "defer.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("VelaDeferScope", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("Defer", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["2", "0"], result.StandardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileDeferRunsOnContinueAndBreakAtEachLexicalScopeExit()
    {
        var compilation = Compile("""
            fn mark(value: Int) -> Void {
                print(value);
            }

            fn main() -> Int {
                var value: Int = 0;
                while value < 2 {
                    value = value + 1;
                    defer mark(value);
                    if value == 1 {
                        continue;
                    }
                    break;
                }
                return 0;
            }
            """, "defer-loop-control.vela");

        Assert.False(compilation.HasErrors);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("DeferLoopControl", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["1", "2"], result.StandardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileSourceLibraryLinksVelaCodeAndResolvesPackageAlias()
    {
        var libraryPath = Path.Combine("packages", "vela.std.config", "src", "lib.vela");
        var applicationPath = Path.Combine("apps", "sample", "src", "main.vela");
        var compilation = VelaCompiler.Compile(
        [
            new VelaSourceDocument(new SourceText("""
                include vela.core.json as json;

                fn config_compact(value: Text) -> Text {
                    return json.compact(value);
                }
                """, libraryPath), "vela.std.config"),
            new VelaSourceDocument(new SourceText("""
                include vela.std.config as config;

                fn main() -> Int {
                    print(config.compact("{ \"ready\": true }"));
                    return 0;
                }
                """, applicationPath))
        ]);

        Assert.False(compilation.HasErrors);
        Assert.Contains("lib.vela", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("SourceLibrary", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("{\"ready\":true}", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileSourceLibraryMapsDiagnosticsToOwningDocument()
    {
        var libraryPath = Path.Combine("packages", "vela.std.config", "src", "lib.vela");
        var compilation = VelaCompiler.Compile(
        [
            new VelaSourceDocument(new SourceText("fn config_broken(value Int) -> Int { return value; }", libraryPath), "vela.std.config"),
            new VelaSourceDocument(new SourceText("fn main() -> Int { return 0; }", "apps/sample/src/main.vela"))
        ]);

        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics, static item => item.Code == "P001");
        var mapped = compilation.MapDiagnostic(diagnostic);
        Assert.Equal(libraryPath, mapped.Source.FilePath);
    }

    [Fact]
    public async Task CompileAsyncFunctionsAwaitFutureAndRunFromAsyncMain()
    {
        var compilation = Compile("""
            async fn answer() -> Int {
                return 42;
            }

            async fn main() -> Int {
                let value = await answer();
                print(value);
                return 0;
            }
            """, "async.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("async Task<int> answer()", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("await answer()", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("main().GetAwaiter().GetResult()", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("Async", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
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
    public void CompileAwaitOutsideAsyncFunctionReportsDiagnostic()
    {
        var compilation = Compile("""
            async fn answer() -> Int { return 42; }
            fn main() -> Int {
                let value = await answer();
                return value;
            }
            """, "invalid-await.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VEL3019" && diagnostic.Message.Contains("only valid inside an async", StringComparison.Ordinal));
    }

    [Fact]
    public void CompileAsyncTcpCallsUseFutureAndCancellationBindings()
    {
        var compilation = Compile("""
            include vela.core.tcp as tcp;
            include vela.concurrent as concurrent;

            async fn connect() -> TcpConnection {
                let cancellation = concurrent.create();
                return await tcp.connect_async("127.0.0.1", 7007, 1000, cancellation);
            }

            fn main() -> Int { return 0; }
            """, "async-tcp.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("TcpConnection.ConnectAsync", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("VelaCancellation.Create()", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileAsyncInterfaceMethodPreservesTaskContract()
    {
        var compilation = Compile("""
            interface CounterSource {
                async fn next() -> Int;
            }

            class Counter() implements CounterSource {
                async fn next() -> Int {
                    return 7;
                }
            }

            async fn main() -> Int {
                let counter = Counter();
                print(await counter.next());
                return 0;
            }
            """, "async-interface.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("Task<int> next();", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("AsyncInterface", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("7", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileOfficialSourcePackagesRoutesJsonThroughVelaCoreJson()
    {
        var root = FindRepositoryRoot();
        var packages = new[] { "vela.std.cli", "vela.std.config", "vela.std.log", "vela.std.test", "vela.std.http" };
        var documents = packages
            .Select(package => new VelaSourceDocument(
                new SourceText(File.ReadAllText(Path.Combine(root, "packages", package, "src", "lib.vela")), Path.Combine(root, "packages", package, "src", "lib.vela")),
                package))
            .Append(new VelaSourceDocument(new SourceText("""
                include vela.std.log as log;
                include vela.std.http as http;

                fn main() -> Int {
                    let payload = http.compact_json("{ \"enabled\": true }");
                    log.json(payload);
                    return 0;
                }
                """, "official-packages.vela")))
            .ToArray();

        var compilation = VelaCompiler.Compile(documents);

        Assert.False(compilation.HasErrors);
        Assert.Contains("VelaJson.Compact", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonDocument", compilation.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileCliAndLoggingPackagesHandlesDefaultsAliasesSubcommandsAndErrors()
    {
        var root = FindRepositoryRoot();
        var documents = CliLogPackageNames
            .Select(package => new VelaSourceDocument(
                new SourceText(File.ReadAllText(Path.Combine(root, "packages", package, "src", "lib.vela")), Path.Combine(root, "packages", package, "src", "lib.vela")),
                package))
            .Append(new VelaSourceDocument(new SourceText("""
                include vela.std.cli as cli;
                include vela.std.log as log;

                fn main() -> Int {
                    let root = cli.command("tool", "integration test", "0.2.0");
                    root.option("--name", "Name", default_value: "Vela", alias: "-n");
                    root.flag("--json", "JSON mode");
                    let serve = cli.command("serve", "serve command");
                    serve.option("--port", "Port", default_value: "8080");
                    root.subcommand(serve);

                    let parsed = root.parse();
                    if parsed.is_error { print(parsed.error); return 2; }
                    print(parsed.command);
                    if parsed.command == "serve" {
                        print(parsed.require_int("--port"));
                        return 0;
                    }

                    print(parsed.require_text("--name"));
                    print(parsed.require_bool("--json"));
                    log.logger("test", json: true, color: false).with_field("name", parsed.require_text("--name")).info("parsed");
                    return 0;
                }
                """, "cli-log-integration.vela")))
            .ToArray();
        var compilation = VelaCompiler.Compile(documents);

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("CliLogIntegration", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var defaults = await VelaBuildService.RunGeneratedProjectAsync(project);
            var aliases = await VelaBuildService.RunGeneratedProjectAsync(project, ["-n", "Ada", "--json"]);
            var subcommand = await VelaBuildService.RunGeneratedProjectAsync(project, ["serve", "--port", "9090"]);
            var invalid = await VelaBuildService.RunGeneratedProjectAsync(project, ["--unknown"]);

            Assert.Equal(0, defaults.ExitCode);
            Assert.Contains("Vela", defaults.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"message\":\"parsed\"", defaults.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(0, aliases.ExitCode);
            Assert.Contains("Ada", aliases.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("True", aliases.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(0, subcommand.ExitCode);
            Assert.Contains("serve", subcommand.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("9090", subcommand.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Contains("Unknown option: --unknown", invalid.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileTryCatchFinallyHandlesVelaIoExceptionAndExposesMetadata()
    {
        var compilation = Compile("""
            include vela.core;
            include vela.core.io as io;

            fn main() -> Int {
                try {
                    let content = io.read_text("__vela_file_that_must_not_exist__");
                    print(content);
                    return 1;
                }
                catch VelaIoException error {
                    print(error.message);
                    let location = error.source_location;
                    print(location.has_value);
                    return 0;
                }
                finally {
                    print("finally");
                }
            }
            """, "exceptions.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains("catch (VelaIoException error)", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("SourceLocation is", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("Exceptions", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("True", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("finally", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileUnreachableVelaExceptionCatchReportsDiagnostic()
    {
        var compilation = Compile("""
            fn main() -> Int {
                try {
                    return 0;
                }
                catch VelaRuntimeException error {
                    return 1;
                }
                catch VelaIoException ioError {
                    return 2;
                }
            }
            """, "invalid-catch-order.vela");

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VEL3018" && diagnostic.Message.Contains("unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileProductivityFeaturesRunsWithVoidEntryPoint()
    {
        var compilation = Compile("""
            include vela.core;

            interface Named {
                fn name() -> Text;
            }

            interface Resettable {
                fn reset() -> Void;
            }

            @since("0.2.0")
            class Worker(worker_name: Text, initial: Int = 2) implements Named, Resettable {
                var value: Int = initial;
                fn name() -> Text { return worker_name; }
                fn reset() -> Void { self.value = 0; }
            }

            record PairData {
                left: Int;
                right: Int;
            }

            fn calculate(base: Int, step: Int = base + 1, times: Int = 2) -> Int {
                return base + step * times;
            }

            fn main() -> Void {
                let worker = Worker(initial: 3, worker_name: "vela");
                print(worker.name());
                print(calculate(4, times: 3));
                let (host, port) = ("localhost", 8080);
                print(host);
                print(port);
                let pair = PairData(7, 8);
                let PairData { left, right } = pair;
                print(left + right);
                let optional = try_unbox<Int>(5);
                match optional {
                    case Some(value) { print(value); }
                    case None { print("none"); }
                }
                worker.reset();
            }
            """, "productivity.vela");

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Contains("private readonly string __velaCtor_worker_name", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("initial: 3", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("__velaMatch", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("Productivity", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("vela", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("19", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("localhost", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("15", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CompileAlgebraicFactoriesAndExhaustiveMatchRuns()
    {
        var compilation = Compile("""
            include vela.core;

            fn main() -> Int {
                let optional = some<Int>(21);
                match optional {
                    case Some(value) { print(value); }
                    case None { return 10; }
                }

                let outcome = ok<Int, Text>(42);
                match outcome {
                    case Ok(value) { print(value); }
                    case Err(error) { print(error); return 20; }
                }
                return 0;
            }
            """, "algebraic-factories.vela");

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Contains("Option.Some<int>(21)", compilation.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("Result.Ok<int, string>(42)", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("AlgebraicFactories", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("21", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("42", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CompileAttributeUsesReportsNonBlockingWarnings()
    {
        var compilation = Compile("""
            @deprecated("Use current instead.")
            fn legacy() -> Void { }

            @experimental
            class Preview() {
                @deprecated("Use run instead.")
                fn execute() -> Void { }
            }

            fn main() -> Void {
                legacy();
                let preview = Preview();
                preview.execute();
            }
            """, "attribute-warnings.vela");

        Assert.False(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VELW002" && diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Message.Contains("Use current", StringComparison.Ordinal));
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VELW003" && diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Message.Contains("Preview", StringComparison.Ordinal));
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "VELW002" && diagnostic.Message.Contains("Preview.execute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileDefaultReferencingEarlierParameterEvaluatesArgumentOnce()
    {
        var compilation = Compile("""
            class Probe() {
                var calls: Int = 0;

                fn next() -> Int {
                    self.calls = self.calls + 1;
                    return self.calls;
                }
            }

            fn total(base: Int, extra: Int = base + 1) -> Int {
                return base + extra;
            }

            fn main() -> Int {
                let probe = Probe();
                print(total(probe.next()));
                print(probe.calls);
                if probe.calls == 1 { return 0; }
                return 9;
            }
            """, "default-evaluation.vela");

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Contains("__velaCallArg", compilation.GeneratedSource, StringComparison.Ordinal);

        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("DefaultEvaluation", outputDirectory, Mode: ExecutableMode.FrameworkDependent));
            var result = await VelaBuildService.RunGeneratedProjectAsync(project);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("3", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("1", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RunGeneratedProjectForwardsOnlyExplicitProgramArguments()
    {
        var compilation = Compile("""
            include vela.core.env as env;

            fn main() -> Int {
                print(env.argument_count());
                if env.argument_count() > 0 {
                    let first = env.argument(0);
                    if first.has_value { print(first.value); }
                }
                return 0;
            }
            """, "program-arguments.vela");

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var project = new VelaBuildService(GetRuntimeProjectPath()).WriteSourceProject(
                compilation,
                new BuildOptions("ProgramArguments", outputDirectory, Mode: ExecutableMode.FrameworkDependent));

            var withoutArguments = await VelaBuildService.RunGeneratedProjectAsync(project);
            var withArguments = await VelaBuildService.RunGeneratedProjectAsync(project, ["--name", "Vela"]);

            Assert.Equal(0, withoutArguments.ExitCode);
            Assert.Equal("0", withoutArguments.StandardOutput.Trim());
            Assert.Equal(0, withArguments.ExitCode);
            Assert.Contains("2", withArguments.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("--name", withArguments.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Theory]
    [InlineData("fn main() -> Void { return 1; }", "VEL3020")]
    [InlineData("fn bad(value: Void) -> Void { }", "VEL3020")]
    [InlineData("class Missing() { value: Int; }", "VEL3021")]
    [InlineData("interface A { fn run() -> Void; } class B() implements A, A { fn run() -> Void { } }", "VEL3022")]
    [InlineData("fn add(value: Int) -> Int { return value; } fn main() -> Int { return add(other: 1); }", "VEL3023")]
    [InlineData("enum State { Ready, Running } fn main() -> Int { let state = State.Ready; match state { case State.Ready { return 0; } } }", "VEL3024")]
    [InlineData("record Pair { left: Int; right: Int; } fn main() -> Int { let pair = Pair(1, 2); let Pair { left } = pair; return left; }", "VEL3025")]
    [InlineData("@unknown\nfn main() -> Void { }", "VEL3026")]
    public void CompileInvalidProductivityRuleReportsStructuredDiagnostic(string code, string expectedCode)
    {
        var compilation = Compile(code, "invalid-productivity.vela");

        var matchingDiagnostics = compilation.Diagnostics.Where(item => item.Code == expectedCode).ToArray();
        Assert.NotEmpty(matchingDiagnostics);
        var diagnostic = matchingDiagnostics[0];
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Span.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Help));
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
