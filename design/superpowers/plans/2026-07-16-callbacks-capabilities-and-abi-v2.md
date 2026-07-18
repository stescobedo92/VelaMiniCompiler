# Callbacks, Capabilities, and ABI v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add typed function values, AOT-safe callbacks, opt-in SDK capabilities, and the complete versioned ABI v2 while retaining ABI v1 imports.

**Architecture:** Extend syntax and semantic types first, then move callback and ABI emission out of `CSharpEmitter` behind focused generators. Keep wire values and handle tables in `Vela.Runtime`; keep manifests, validation, C headers, and generated bindings in `Vela.Backend`. Capabilities are compiler-owned signed metadata and can only add allowlisted project/framework/package references.

**Tech Stack:** .NET 10, C# source generation, Native AOT, `LibraryImport`, `UnmanagedCallersOnly`, xUnit, System.Text.Json source generation.

---

## File map

- Modify `src/Vela.Core/Syntax/Types.cs`: function type syntax.
- Modify `src/Vela.Core/Syntax/Expressions.cs`: lambda expression syntax.
- Modify `src/Vela.Core/Parsing/VelaParser.cs`: parse `Fn<(...) , R>`, method groups, and lambdas.
- Modify `src/Vela.Backend/VelaType.cs`: semantic function type and exact signature comparison.
- Create `src/Vela.Backend/Emission/VelaCallbackEmitter.cs`: delegate and closure lowering.
- Create `src/Vela.Backend/Capabilities/VelaCapabilityCatalog.cs`: catalog loading and validation.
- Create `src/Vela.Backend/Capabilities/VelaCapability.cs`: immutable capability model.
- Create `eng/capabilities/vela-capabilities.json`: signed SDK capability data.
- Modify `src/Vela.Backend/BuildOptions.cs`: selected capabilities.
- Modify `src/Vela.Backend/VelaBuildService.cs`: validated project/framework/package references.
- Create `src/Vela.Runtime/Interop/VelaAbiStatus.cs`: status and error categories.
- Create `src/Vela.Runtime/Interop/VelaAbiBuffer.cs`: UTF-8 and flat buffer wire values.
- Create `src/Vela.Runtime/Interop/VelaHandleTable.cs`: generation-checked reference handles.
- Create `src/Vela.Runtime/Interop/VelaTaskHandleTable.cs`: async operation handles.
- Replace ABI v1-only records in `src/Vela.Backend/VelaAbi.cs` with versioned descriptors.
- Create `src/Vela.Backend/Abi/VelaAbiEmitter.cs`: ABI v2 export/import emission.
- Create `src/Vela.Backend/Abi/VelaAbiHeaderWriter.cs`: deterministic C11 header.
- Modify `src/Vela.Backend/CSharpEmitter.cs`: delegate callback and ABI work.
- Modify `src/Vela.Backend/VelaJsonSerializerContext.cs`: new manifest/catalog contexts.
- Test `tests/Vela.Core.Tests/VelaParserTests.cs`.
- Create `tests/Vela.Backend.Tests/CallbackAndCapabilityTests.cs`.
- Create `tests/Vela.Backend.Tests/VelaAbiV2Tests.cs`.
- Create `tests/Vela.Runtime.Tests/VelaHandleTableTests.cs`.
- Create `tests/Vela.Runtime.Tests/VelaTaskHandleTableTests.cs`.

### Task 1: Parse function types and lambdas

**Files:**
- Modify: `src/Vela.Core/Syntax/Types.cs`
- Modify: `src/Vela.Core/Syntax/Expressions.cs`
- Modify: `src/Vela.Core/Parsing/VelaParser.cs`
- Test: `tests/Vela.Core.Tests/VelaParserTests.cs`

- [ ] **Step 1: Write the failing parser tests**

```csharp
[Fact]
public void ParsesFunctionTypeAndCapturingLambda()
{
    var result = VelaParser.Parse(SourceText.From("""
        fn main() -> Void {
            let prefix = "Hello ";
            let format: Fn<(Text), Text> = fn(name: Text) -> Text {
                return prefix + name;
            };
        }
        """, "callback.vela"));

    Assert.Empty(result.Diagnostics);
    var main = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(result.Root.Members));
    var declaration = Assert.IsType<LetStatementSyntax>(main.Body.Statements[1]);
    var type = Assert.IsType<FunctionTypeSyntax>(declaration.Type);
    Assert.Single(type.ParameterTypes);
    Assert.IsType<LambdaExpressionSyntax>(declaration.Initializer);
}

[Theory]
[InlineData("Fn<(Text),>")]
[InlineData("fn(value: Text) Text { return value; }")]
public void ReportsIncompleteFunctionSyntax(string source)
{
    var result = VelaParser.Parse(SourceText.From($"fn main() -> Void {{ let value = {source}; }}", "invalid-callback.vela"));
    Assert.NotEmpty(result.Diagnostics);
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test tests/Vela.Core.Tests/Vela.Core.Tests.csproj --configuration Release --filter FullyQualifiedName~Function --no-restore`

Expected: FAIL because `FunctionTypeSyntax` and `LambdaExpressionSyntax` do not exist.

- [ ] **Step 3: Add the exact syntax nodes and parser branches**

```csharp
public sealed record FunctionTypeSyntax(
    SyntaxToken FnKeyword,
    SyntaxToken LessToken,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<TypeSyntax> ParameterTypes,
    SyntaxToken RightParenthesis,
    SyntaxToken Comma,
    TypeSyntax ReturnType,
    SyntaxToken GreaterToken,
    bool IsAsync) : TypeSyntax
{
    public override TextSpan Span => TextSpan.FromBounds(FnKeyword.Span.Start, GreaterToken.Span.End);
}

public sealed record LambdaExpressionSyntax(
    SyntaxToken? AsyncKeyword,
    SyntaxToken FnKeyword,
    SyntaxToken LeftParenthesis,
    IReadOnlyList<ParameterSyntax> Parameters,
    SyntaxToken RightParenthesis,
    SyntaxToken Arrow,
    TypeSyntax ReturnType,
    BlockSyntax Body) : ExpressionSyntax
{
    public override TextSpan Span => TextSpan.FromBounds((AsyncKeyword ?? FnKeyword).Span.Start, Body.Span.End);
}
```

Add `ParseFunctionType(bool isAsync)` to `ParseType`, and add `ParseLambdaExpression()` to the primary-expression switch when the current token is `fn` or `async fn`. Reuse `ParseParameters`, `ParseType`, and `ParseBlock`; do not duplicate parameter parsing.

- [ ] **Step 4: Run parser tests**

Run: `dotnet test tests/Vela.Core.Tests/Vela.Core.Tests.csproj --configuration Release --no-restore`

Expected: PASS with zero failed tests.

- [ ] **Step 5: Commit the syntax increment**

```powershell
git add src/Vela.Core/Syntax/Types.cs src/Vela.Core/Syntax/Expressions.cs src/Vela.Core/Parsing/VelaParser.cs tests/Vela.Core.Tests/VelaParserTests.cs
git commit -m "feat: parse typed callbacks and lambdas"
```

### Task 2: Bind and emit AOT-safe callbacks

**Files:**
- Modify: `src/Vela.Backend/VelaType.cs`
- Create: `src/Vela.Backend/Emission/VelaCallbackEmitter.cs`
- Modify: `src/Vela.Backend/CSharpEmitter.cs`
- Test: `tests/Vela.Backend.Tests/CallbackAndCapabilityTests.cs`

- [ ] **Step 1: Write failing semantic and emission tests**

```csharp
[Fact]
public void CapturingLambdaEmitsSealedClosureAndTypedDelegate()
{
    var compilation = VelaCompiler.Compile(SourceText.From("""
        fn main() -> Int {
            let prefix = "Hello ";
            let format: Fn<(Text), Text> = fn(name: Text) -> Text {
                return prefix + name;
            };
            print(format("Vela"));
            return 0;
        }
        """, "callback.vela"));

    Assert.Empty(compilation.Diagnostics);
    Assert.Contains("private sealed class VelaClosure_", compilation.GeneratedSource, StringComparison.Ordinal);
    Assert.Contains("System.Func<string, string>", compilation.GeneratedSource, StringComparison.Ordinal);
}

[Fact]
public void CallbackSignatureMismatchIsAVelaDiagnostic()
{
    var compilation = VelaCompiler.Compile(SourceText.From("""
        fn main() -> Void {
            let callback: Fn<(Int), Text> = fn(value: Text) -> Text { return value; };
        }
        """, "bad-callback.vela"));
    Assert.Contains(compilation.Diagnostics, item => item.Code == "VEL3040");
}
```

- [ ] **Step 2: Verify the backend tests fail**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~Callback --no-restore`

Expected: FAIL because function values are not semantic types or expressions.

- [ ] **Step 3: Implement exact function signatures and callback emission**

```csharp
public sealed record VelaFunctionSignature(
    IReadOnlyList<VelaType> Parameters,
    VelaType ReturnType,
    bool IsAsync)
{
    public bool ExactlyMatches(VelaFunctionSignature other) =>
        IsAsync == other.IsAsync
        && ReturnType.IsSameAs(other.ReturnType)
        && Parameters.Count == other.Parameters.Count
        && Parameters.Zip(other.Parameters).All(pair => pair.First.IsSameAs(pair.Second));
}
```

Represent a function as `new VelaType("Fn", signature.Parameters.Append(signature.ReturnType).ToArray(), IsAsync: signature.IsAsync)`. `VelaCallbackEmitter` must expose `EmitLambda`, `EmitMethodGroup`, and `CSharpDelegateType`; non-capturing lambdas use a `static readonly` delegate, and capturing lambdas use a generated sealed class with readonly captured fields. Add diagnostic `VEL3040` for exact signature mismatch and `VEL3041` for an unsafe escaping capture.

- [ ] **Step 4: Run all backend tests**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --no-restore`

Expected: PASS with generated code free of dynamic invocation and reflection.

- [ ] **Step 5: Commit callback lowering**

```powershell
git add src/Vela.Backend/VelaType.cs src/Vela.Backend/Emission/VelaCallbackEmitter.cs src/Vela.Backend/CSharpEmitter.cs tests/Vela.Backend.Tests/CallbackAndCapabilityTests.cs
git commit -m "feat: lower typed callbacks for native aot"
```

### Task 3: Add the allowlisted capability catalog

**Files:**
- Create: `src/Vela.Backend/Capabilities/VelaCapability.cs`
- Create: `src/Vela.Backend/Capabilities/VelaCapabilityCatalog.cs`
- Create: `eng/capabilities/vela-capabilities.json`
- Modify: `src/Vela.Backend/BuildOptions.cs`
- Modify: `src/Vela.Backend/VelaBuildService.cs`
- Modify: `src/Vela.Backend/VelaJsonSerializerContext.cs`
- Test: `tests/Vela.Backend.Tests/CallbackAndCapabilityTests.cs`

- [ ] **Step 1: Write failing catalog validation tests**

```csharp
[Fact]
public void CatalogRejectsUnknownCapabilityAndArbitraryMsBuild()
{
    var catalog = VelaCapabilityCatalog.Load(TestCapabilityCatalog.ValidPath);
    Assert.Throws<VelaCapabilityException>(() => catalog.Resolve(["unknown"]));
    Assert.Throws<VelaCapabilityException>(() => VelaCapabilityCatalog.Load(TestCapabilityCatalog.WithImportProject));
}
```

- [ ] **Step 2: Verify the test fails**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~Capability --no-restore`

Expected: FAIL because the catalog does not exist.

- [ ] **Step 3: Implement immutable capability models and allowlisted project generation**

```csharp
public sealed record VelaCapability(
    string Id,
    string? ProjectSdk,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<VelaManagedPackage> PackageReferences,
    IReadOnlyList<string> SupportedTargets);

public sealed record VelaManagedPackage(string Id, string Version);

public sealed record VelaCapabilityCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<VelaCapability> Capabilities,
    string Sha256,
    string SigningKeyId,
    string Signature);
```

The catalog JSON must define `aspnet-server`, `sqlite`, `postgres`, and `uno-ui`; use exact managed versions `Microsoft.Data.Sqlite` `10.0.10`, `Npgsql` `10.0.3`, and `Uno.Sdk` `6.5.36`. Verify SHA-256 and the ECDSA P-256 signature against the SDK-pinned capability signing key before resolving any entry. `VelaBuildService` renders only `Sdk`, `FrameworkReference`, and `PackageReference` elements from validated records. Reject XML metacharacters in identifiers, non-exact versions, duplicate IDs, relative SDK paths, MSBuild imports, targets, analyzers, and build assets.

- [ ] **Step 4: Run capability and generated-project tests**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter "FullyQualifiedName~Capability|FullyQualifiedName~GeneratedProject" --no-restore`

Expected: PASS; generated project snapshots contain only allowlisted references.

- [ ] **Step 5: Commit the capability catalog**

```powershell
git add eng/capabilities src/Vela.Backend/Capabilities src/Vela.Backend/BuildOptions.cs src/Vela.Backend/VelaBuildService.cs src/Vela.Backend/VelaJsonSerializerContext.cs tests/Vela.Backend.Tests/CallbackAndCapabilityTests.cs
git commit -m "feat: add allowlisted sdk capabilities"
```

### Task 4: Implement ABI v2 runtime wire values and handles

**Files:**
- Create: `src/Vela.Runtime/Interop/VelaAbiStatus.cs`
- Create: `src/Vela.Runtime/Interop/VelaAbiBuffer.cs`
- Create: `src/Vela.Runtime/Interop/VelaHandleTable.cs`
- Create: `src/Vela.Runtime/Interop/VelaTaskHandleTable.cs`
- Test: `tests/Vela.Runtime.Tests/VelaHandleTableTests.cs`
- Test: `tests/Vela.Runtime.Tests/VelaTaskHandleTableTests.cs`

- [ ] **Step 1: Write failing ownership and stale-handle tests**

```csharp
[Fact]
public void ReleasedHandleCannotResolveAfterSlotReuse()
{
    using var table = new VelaHandleTable(ownerId: 7);
    var first = table.Create("first", VelaTypeContract.Text);
    Assert.True(table.Release(first).IsSuccess);
    var second = table.Create("second", VelaTypeContract.Text);
    Assert.NotEqual(first.Generation, second.Generation);
    Assert.Equal(VelaAbiStatus.StaleHandle, table.Resolve<string>(first, VelaTypeContract.Text).Status);
}

[Fact]
public async Task ReleasingIncompleteTaskRequestsCancellationThenDisposes()
{
    using var tasks = new VelaTaskHandleTable(ownerId: 7);
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handle = tasks.Create(async token => { await Task.Delay(Timeout.Infinite, token); }, observed);
    Assert.True(tasks.Release(handle).IsSuccess);
    await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
}
```

- [ ] **Step 2: Run the focused runtime tests and verify failure**

Run: `dotnet test tests/Vela.Runtime.Tests/Vela.Runtime.Tests.csproj --configuration Release --filter "FullyQualifiedName~HandleTable" --no-restore`

Expected: FAIL because ABI v2 handle tables do not exist.

- [ ] **Step 3: Implement fixed-layout statuses, buffers, and handles**

```csharp
public enum VelaAbiStatus : int
{
    Success = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    WrongOwner = 3,
    WrongType = 4,
    StaleHandle = 5,
    Cancelled = 6,
    TimedOut = 7,
    InternalError = 255
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct VelaHandle(ulong Owner, uint Slot, uint Generation, ulong TypeContract);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct VelaAbiBuffer(nint Data, nuint Length, nuint Capacity, ulong AllocatorId);
```

Use a lock-protected slot array plus free list. Increment generation before reuse, checked for overflow. `Retain` and `Release` use checked reference counts; final release disposes `IDisposable`/`IAsyncDisposable` values exactly once. `VelaTaskHandleTable` owns a linked `CancellationTokenSource`, completion state, one-time result extraction, and deferred disposal after incomplete release.

- [ ] **Step 4: Run runtime tests and stress them repeatedly**

Run: `dotnet test tests/Vela.Runtime.Tests/Vela.Runtime.Tests.csproj --configuration Release --no-restore`

Run: `1..20 | ForEach-Object { dotnet test tests/Vela.Runtime.Tests/Vela.Runtime.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~HandleTable" }`

Expected: every run passes with no hangs or double disposal.

- [ ] **Step 5: Commit ABI runtime ownership**

```powershell
git add src/Vela.Runtime/Interop tests/Vela.Runtime.Tests/VelaHandleTableTests.cs tests/Vela.Runtime.Tests/VelaTaskHandleTableTests.cs
git commit -m "feat: add abi v2 ownership runtime"
```

### Task 5: Version ABI manifests and validate contracts

**Files:**
- Modify: `src/Vela.Backend/VelaAbi.cs`
- Create: `src/Vela.Backend/Abi/VelaAbiTypeDescriptor.cs`
- Modify: `src/Vela.Backend/VelaJsonSerializerContext.cs`
- Test: `tests/Vela.Backend.Tests/VelaAbiV2Tests.cs`

- [ ] **Step 1: Write failing canonical-hash and v1 compatibility tests**

```csharp
[Fact]
public void AbiV2ContractHashChangesWithOwnershipAndLayout()
{
    var borrowed = VelaAbiManifestV2.Create(TestAbi.Package, [TestAbi.TextParameter("borrowed")]);
    var owned = VelaAbiManifestV2.Create(TestAbi.Package, [TestAbi.TextParameter("owned")]);
    Assert.NotEqual(borrowed.ContractHash, owned.ContractHash);
}

[Fact]
public void ReaderAcceptsExistingAbiV1Manifest()
{
    var manifest = VelaAbiManifestReader.Read(TestAbi.ExistingV1Json);
    Assert.Equal(1, manifest.AbiVersion);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~AbiV2 --no-restore`

Expected: FAIL because v2 descriptors and the versioned reader do not exist.

- [ ] **Step 3: Implement the versioned manifest model**

```csharp
public enum VelaAbiOwnership { Borrowed, Owned, Shared }
public enum VelaAbiTypeKind { Unit, Scalar, Decimal, Text, Record, Option, Result, Buffer, Handle, Callback, Task }

public sealed record VelaAbiTypeDescriptor(
    VelaAbiTypeKind Kind,
    string ContractId,
    int Size,
    int Alignment,
    bool Nullable,
    VelaAbiOwnership Ownership,
    IReadOnlyList<VelaAbiFieldDescriptor> Fields,
    IReadOnlyList<VelaAbiTypeDescriptor> TypeArguments);
```

Create `VelaAbiManifestReader` that inspects `AbiVersion`, deserializes v1 or v2 through generated JSON contexts, canonicalizes every descriptor in ordinal order, and validates target, calling convention, layout, lifecycle symbols, and SHA-256. Preserve the public v1 record shape so existing callers compile.

- [ ] **Step 4: Run manifest tests**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter "FullyQualifiedName~AbiV2|FullyQualifiedName~CompileConsumerBindsDeclaredLibraryImport" --no-restore`

Expected: PASS for both v1 and v2 fixtures.

- [ ] **Step 5: Commit manifest versioning**

```powershell
git add src/Vela.Backend/VelaAbi.cs src/Vela.Backend/Abi/VelaAbiTypeDescriptor.cs src/Vela.Backend/VelaJsonSerializerContext.cs tests/Vela.Backend.Tests/VelaAbiV2Tests.cs
git commit -m "feat: define versioned abi v2 contracts"
```

### Task 6: Generate ABI v2 bindings and C headers

**Files:**
- Create: `src/Vela.Backend/Abi/VelaAbiEmitter.cs`
- Create: `src/Vela.Backend/Abi/VelaAbiHeaderWriter.cs`
- Modify: `src/Vela.Backend/CSharpEmitter.cs`
- Modify: `src/Vela.Backend/VelaBuildService.cs`
- Test: `tests/Vela.Backend.Tests/VelaAbiV2Tests.cs`

- [ ] **Step 1: Write failing text/decimal/handle/callback/header tests**

```csharp
[Fact]
public void AbiV2EmitsStatusOutResultLifecycleAndCHeader()
{
    var library = VelaCompiler.CompileLibrary(SourceText.From("""
        public ffi fn echo(value: Text) -> Text { return value; }
        """, "echo.vela"), "acme.echo", "1.0.0", "win-x64");

    Assert.Empty(library.Compilation.Diagnostics);
    Assert.Contains("VelaAbiStatus", library.Compilation.GeneratedSource, StringComparison.Ordinal);
    Assert.Contains("out VelaAbiBuffer result", library.Compilation.GeneratedSource, StringComparison.Ordinal);
    var header = VelaAbiHeaderWriter.Write(library.ManifestV2!);
    Assert.Contains("vela_status acme_echo_echo(vela_slice value, vela_buffer* result);", header, StringComparison.Ordinal);
    Assert.Contains("void vela_buffer_release(vela_buffer value);", header, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~AbiV2Emits --no-restore`

Expected: FAIL because the v2 emitter and header writer do not exist.

- [ ] **Step 3: Implement status/out-result wrappers and focused emitters**

`VelaAbiEmitter.EmitExport` must wrap the Vela call in `try/catch`, validate incoming wire values, store success into an out-result, and convert all exceptions to `VelaAbiStatus`. `EmitImport` must allocate/copy/release according to the descriptor and map non-success status through `VelaAbiExceptionMapper`. Emit callback thunks as cdecl function pointers plus context handles and async functions as task handles.

```csharp
public static VelaAbiStatus InvokeText(VelaAbiSlice value, out VelaAbiBuffer result)
{
    result = default;
    try
    {
        var managed = VelaUtf8.Decode(value);
        result = VelaAbiAllocator.AllocateUtf8(Program.echo(managed));
        return VelaAbiStatus.Success;
    }
    catch (Exception exception)
    {
        return VelaAbiErrors.Capture(exception);
    }
}
```

`VelaAbiHeaderWriter` must use LF, sorted declarations, fixed-width `<stdint.h>` types, `extern "C"`, platform export/calling-convention macros, and the manifest contract hash comment. `VelaBuildService.BuildLibraryAsync` writes `<library>.velaabi.json` and `<library>.h` atomically next to the shared library.

- [ ] **Step 4: Run complete build/test/native smoke**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- build examples/packages/vela-math --release --lib`

Expected: build and tests pass; the library output contains a v2 manifest and deterministic C header; existing ABI v1 import tests pass.

- [ ] **Step 5: Commit ABI v2 generation**

```powershell
git add src/Vela.Backend/Abi src/Vela.Backend/CSharpEmitter.cs src/Vela.Backend/VelaBuildService.cs tests/Vela.Backend.Tests/VelaAbiV2Tests.cs
git commit -m "feat: generate abi v2 bindings and headers"
```

### Task 7: Final foundation verification

**Files:**
- Modify: `docs/articles/language-tour.md`
- Modify: `docs/articles/architecture.md`
- Modify: `docs/Vela-language.md`

- [ ] **Step 1: Add checked callback and ABI v2 documentation examples**

Document the exact `Fn<(P), R>` syntax, closure lifetime rules, ABI ownership table, v1 compatibility, status mapping, and generated header usage. Copy code only from fixtures introduced above.

- [ ] **Step 2: Run documentation example checks and DocFX**

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- check examples/types-objects.vela`

Run: `docfx docs/docfx.json --warningsAsErrors`

Expected: both commands exit 0.

- [ ] **Step 3: Run whitespace and repository checks**

Run: `git diff --check`

Run: `git status --short`

Expected: no whitespace errors; only intended documentation changes remain.

- [ ] **Step 4: Commit the foundation documentation**

```powershell
git add docs/articles/language-tour.md docs/articles/architecture.md docs/Vela-language.md
git commit -m "docs: document callbacks and abi v2"
```

- [ ] **Step 5: Record the gate result in the execution log**

Append the exact build, test, Native AOT smoke, and DocFX command outcomes to the implementation session summary; do not create a repository file solely for the execution log.
