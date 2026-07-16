# Uno UI and Hello Form Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add modular `vela.ui.*` packages backed by Uno C# Markup and ship the exact tested Hello World form for desktop, mobile, and web targets.

**Architecture:** Lower Vela declarative nodes and typed callbacks into a small reflection-free UI description/runtime. Render through an injectable backend: a deterministic headless backend for tests and an Uno C# Markup backend for production. Generate a pinned Uno project only when the `uno-ui` capability is selected.

**Tech Stack:** Uno.Sdk 6.5.36, C# Markup, .NET 10, WinUI/Skia, Android, iOS, Mac Catalyst, WebAssembly, Native AOT where supported, xUnit.

---

## File map

- Create `src/Vela.Ui.Runtime` and `tests/Vela.Ui.Runtime.Tests`.
- Create `src/Vela.Backend/Ui/VelaUiProjectWriter.cs` and `VelaUiBindingEmitter.cs`.
- Create packages `vela.ui`, `.components`, `.forms`, `.layout`, `.navigation`, `.state`, `.platform`.
- Create `examples/ui-hello-form` with the exact approved source.
- Add UI target workflow and documentation.

### Task 1: Generate an isolated pinned Uno project

**Files:**
- Create: `src/Vela.Ui.Runtime/Vela.Ui.Runtime.csproj`
- Create: `src/Vela.Backend/Ui/VelaUiProjectWriter.cs`
- Modify: `src/Vela.Backend/VelaBuildService.cs`
- Modify: `eng/capabilities/vela-capabilities.json`
- Modify: `Vela.slnx`
- Test: `tests/Vela.Backend.Tests/VelaUiProjectWriterTests.cs`

- [ ] **Step 1: Write failing project snapshot tests**

```csharp
[Fact]
public void UiProjectPinsUnoAndIncludesOnlyRequestedTarget()
{
    var project = VelaUiProjectWriter.Write(TestBuild.Layout, new VelaUiTarget(VelaUiPlatform.Desktop, "win-x64"));
    var xml = File.ReadAllText(project.ProjectPath);
    Assert.Contains("Sdk=\"Uno.Sdk/6.5.36\"", xml, StringComparison.Ordinal);
    Assert.Contains("<UnoFeatures>Material;CSharpMarkup</UnoFeatures>", xml, StringComparison.Ordinal);
    Assert.DoesNotContain("Microsoft.Data.Sqlite", xml, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~VelaUiProjectWriter --no-restore`

Expected: FAIL because the UI writer/runtime are absent.

- [ ] **Step 3: Implement platform mapping and deterministic project XML**

```csharp
public enum VelaUiPlatform { Desktop, Windows, Android, Ios, MacCatalyst, BrowserWasm }
public sealed record VelaUiTarget(VelaUiPlatform Platform, string RuntimeIdentifier);
```

Map `desktop` to Uno Skia Desktop (Windows/Linux/macOS), `windows` to Windows App SDK, `android`, `ios`, `maccatalyst`, and `browserwasm` to their Uno TFMs from the pinned 6.5.36 SDK. `VelaUiProjectWriter` emits one target per build request, `UnoFeatures=Material;CSharpMarkup`, invariant properties, trimming, and a reference to the staged `Vela.Ui.Runtime` project. Reject Apple builds off macOS and target/toolchain mismatches before restore.

- [ ] **Step 4: Restore/build the generated desktop fixture**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~VelaUiProjectWriter --no-restore`

Run: `dotnet build tests/Vela.Backend.Tests/Fixtures/UnoDesktop/UnoDesktop.csproj --configuration Release`

Expected: PASS with Uno.Sdk 6.5.36 and no floating versions.

- [ ] **Step 5: Commit UI project generation**

```powershell
git add Vela.slnx src/Vela.Ui.Runtime src/Vela.Backend/Ui/VelaUiProjectWriter.cs src/Vela.Backend/VelaBuildService.cs eng/capabilities/vela-capabilities.json tests/Vela.Backend.Tests/VelaUiProjectWriterTests.cs tests/Vela.Backend.Tests/Fixtures/UnoDesktop
git commit -m "feat: generate pinned uno projects"
```

### Task 2: Implement state, nodes, lifecycle, and headless rendering

**Files:**
- Create: `src/Vela.Ui.Runtime/State/VelaState.cs`
- Create: `src/Vela.Ui.Runtime/Tree/VelaUiNode.cs`
- Create: `src/Vela.Ui.Runtime/Tree/VelaUiRenderer.cs`
- Create: `src/Vela.Ui.Runtime/Hosting/IVelaUiBackend.cs`
- Create: `src/Vela.Ui.Runtime/Hosting/HeadlessUiBackend.cs`
- Test: `tests/Vela.Ui.Runtime.Tests/StateAndLifecycleTests.cs`

- [ ] **Step 1: Write failing state/lifecycle tests**

```csharp
[Fact]
public void StateUpdatesBoundNodeAndDisposalUnsubscribesExactlyOnce()
{
    var state = new VelaState<string>("Hello World");
    using var backend = new HeadlessUiBackend();
    using var renderer = new VelaUiRenderer(backend);
    var node = VelaUi.Text(state);
    renderer.Mount(node);
    state.Set("Vela");
    Assert.Equal("Vela", backend.Text(node.Id));
    renderer.Dispose();
    Assert.Equal(0, state.SubscriberCount);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~StateAndLifecycle`

Expected: FAIL because the UI model is absent.

- [ ] **Step 3: Implement immutable nodes and dispatcher-checked state**

```csharp
public sealed class VelaState<T>
{
    public T Value { get; private set; }
    public event Action<T>? Changed;
    public void Set(T value) { Value = value; Changed?.Invoke(value); }
}

public abstract record VelaUiNode(long Id, VelaAccessibility Accessibility);
public sealed record VelaTextNode(long Id, VelaState<string> Text, VelaAccessibility Accessibility) : VelaUiNode(Id, Accessibility);
```

Add stable node IDs, explicit parent/child ownership, mount/update/unmount lifecycle, dispatcher enforcement, batched property updates, and idempotent disposal. `IVelaUiBackend` defines create/update/remove/focus/dialog/navigation operations; tests never load Uno.

- [ ] **Step 4: Run UI runtime tests**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release`

Expected: PASS for state, computed state, batching, thread violations, tree diffing, callback release, and double disposal.

- [ ] **Step 5: Commit UI foundations**

```powershell
git add src/Vela.Ui.Runtime/State src/Vela.Ui.Runtime/Tree src/Vela.Ui.Runtime/Hosting tests/Vela.Ui.Runtime.Tests
git commit -m "feat: add declarative ui runtime"
```

### Task 3: Add components, layouts, themes, and Uno backend

**Files:**
- Create: `src/Vela.Ui.Runtime/Components/*.cs`
- Create: `src/Vela.Ui.Runtime/Layout/*.cs`
- Create: `src/Vela.Ui.Runtime/Theming/*.cs`
- Create: `src/Vela.Ui.Runtime/Hosting/UnoUiBackend.cs`
- Test: `tests/Vela.Ui.Runtime.Tests/ComponentAndThemeTests.cs`

- [ ] **Step 1: Write failing component mapping tests**

```csharp
[Fact]
public void ButtonInputAndColumnMapToTypedUnoControls()
{
    var backend = new UnoUiBackend();
    var input = VelaUi.TextInput("Message", new VelaState<string>("Hello World"));
    var button = VelaUi.Button("Show", static () => { });
    var column = VelaUi.Column(input, button);
    var root = backend.Create(column);
    Assert.IsType<Microsoft.UI.Xaml.Controls.StackPanel>(root);
    Assert.Contains(((StackPanel)root).Children, child => child is TextBox);
    Assert.Contains(((StackPanel)root).Children, child => child is Button);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~ComponentAndTheme`

Expected: FAIL because production components/backend are absent.

- [ ] **Step 3: Implement the official component and layout set**

Implement text, button, text input, image, list, table, progress, row, column, grid, stack, scroll, split, spacing, alignment, enabled/visible state, and typed style tokens. `UnoUiBackend` creates C# Markup/WinUI controls directly and stores typed control handles by node ID. Theme tokens cover light/dark/system/high-contrast colors, typography, spacing, focus, hover, pressed, disabled, and validation states.

- [ ] **Step 4: Run headless and Uno mapping tests**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release`

Expected: PASS without runtime XAML parsing or reflection.

- [ ] **Step 5: Commit components and Uno rendering**

```powershell
git add src/Vela.Ui.Runtime/Components src/Vela.Ui.Runtime/Layout src/Vela.Ui.Runtime/Theming src/Vela.Ui.Runtime/Hosting/UnoUiBackend.cs tests/Vela.Ui.Runtime.Tests/ComponentAndThemeTests.cs
git commit -m "feat: render official ui components with uno"
```

### Task 4: Add forms, validation, accessibility, and navigation

**Files:**
- Create: `src/Vela.Ui.Runtime/Forms/*.cs`
- Create: `src/Vela.Ui.Runtime/Accessibility/*.cs`
- Create: `src/Vela.Ui.Runtime/Navigation/*.cs`
- Test: `tests/Vela.Ui.Runtime.Tests/FormAccessibilityNavigationTests.cs`

- [ ] **Step 1: Write failing validation/accessibility tests**

```csharp
[Fact]
public void RequiredFieldBlocksSubmitAndAssociatesAccessibleError()
{
    var value = new VelaState<string>("");
    var form = VelaUi.Form("Account").Field(VelaUi.TextInput("Name", value).Required()).Action(VelaUi.Button("Save", static () => { }));
    var result = form.Validate();
    Assert.False(result.IsValid);
    Assert.Equal("Name is required.", result.Errors[0].Message);
    Assert.Equal(form.Fields[0].Node.Id, result.Errors[0].NodeId);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~FormAccessibilityNavigation`

Expected: FAIL because forms/accessibility/navigation are absent.

- [ ] **Step 3: Implement typed validators and platform semantics**

Add required, length, range, pattern, and typed custom validators; summary and inline errors; disabled submit while invalid; accessible name/description/role/state/live-region; deterministic focus order and keyboard activation. Add page stack, typed route parameters, tabs, dialogs, back navigation, deep-link parsing, and lifecycle callbacks with cancellation.

- [ ] **Step 4: Run form/navigation tests**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release`

Expected: PASS including screen-reader metadata snapshots and disposal after navigation.

- [ ] **Step 5: Commit application UI services**

```powershell
git add src/Vela.Ui.Runtime/Forms src/Vela.Ui.Runtime/Accessibility src/Vela.Ui.Runtime/Navigation tests/Vela.Ui.Runtime.Tests/FormAccessibilityNavigationTests.cs
git commit -m "feat: add accessible forms and navigation"
```

### Task 5: Expose modular `vela.ui.*` source packages

**Files:**
- Create: `packages/vela.ui/**`
- Create: `packages/vela.ui.components/**`
- Create: `packages/vela.ui.forms/**`
- Create: `packages/vela.ui.layout/**`
- Create: `packages/vela.ui.navigation/**`
- Create: `packages/vela.ui.state/**`
- Create: `packages/vela.ui.platform/**`
- Create: `src/Vela.Backend/Ui/VelaUiBindingEmitter.cs`
- Test: `tests/Vela.Backend.Tests/VelaUiPackageTests.cs`

- [ ] **Step 1: Write failing package-surface tests**

```csharp
[Theory]
[InlineData("vela.ui")]
[InlineData("vela.ui.components")]
[InlineData("vela.ui.forms")]
[InlineData("vela.ui.layout")]
[InlineData("vela.ui.navigation")]
[InlineData("vela.ui.state")]
[InlineData("vela.ui.platform")]
public void OfficialUiPackageCompilesWithUnoCapability(string package) =>
    Assert.Empty(TestCompilation.Import(package).Diagnostics);
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~VelaUiPackage --no-restore`

Expected: FAIL because packages/bindings are absent.

- [ ] **Step 3: Implement source APIs and compile-time bindings**

Reserve `vela.ui.*` in package-name validation. Each package manifest declares `uno-ui`; functions/classes wrap exact `Vela.Ui.Runtime` APIs. `VelaUiBindingEmitter` resolves typed state, nodes, builders, callbacks, and application lifecycle without string-based dynamic dispatch. `vela.ui.platform` exposes capability queries and an explicit target-specific native-control handle whose use marks the package target-restricted.

- [ ] **Step 4: Run compiler and UI tests**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~VelaUiPackage --no-restore`

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release --no-restore`

Expected: PASS; importing one UI module does not import unrelated UI modules or database/HTTP adapters.

- [ ] **Step 5: Commit public UI packages**

```powershell
git add packages/vela.ui* src/Vela.Backend/Ui/VelaUiBindingEmitter.cs tests/Vela.Backend.Tests/VelaUiPackageTests.cs
git commit -m "feat: expose modular vela ui packages"
```

### Task 6: Add and test the exact Hello World form

**Files:**
- Create: `examples/ui-hello-form/vela.toml`
- Create: `examples/ui-hello-form/src/main.vela`
- Create: `examples/ui-hello-form/vela.lock`
- Create: `tests/Vela.Ui.Runtime.Tests/HelloFormTests.cs`

- [ ] **Step 1: Add the approved source unchanged**

```vela
include vela.ui as ui;
include vela.ui.components as components;
include vela.ui.forms as forms;
include vela.ui.state as state;

fn main() -> Int {
    let message = state.value<Text>("Hello World");
    let output = state.value<Text>("");
    let form = forms.form("Greeting")
        .field(components.text_input("Message", message))
        .action(components.button("Show", fn() -> Void {
            output.set(message.get());
        }))
        .content(components.text(output));

    return ui.run(ui.application("Hello form").window(form));
}
```

- [ ] **Step 2: Run check and verify the initial failure**

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- check examples/ui-hello-form`

Expected before package completion: FAIL naming the first missing UI binding; after Task 5 it must PASS.

- [ ] **Step 3: Add the headless interaction test**

```csharp
[Fact]
public async Task HelloFormCopiesInputToOutputWhenShowIsActivated()
{
    using var app = await VelaUiExampleHost.LoadAsync(TestPaths.Example("ui-hello-form"));
    Assert.Equal("Hello World", app.Input("Message").Text);
    app.Input("Message").Text = "Hello from Vela";
    app.Button("Show").Activate();
    Assert.Equal("Hello from Vela", app.TextContent("Hello from Vela").Text);
}
```

- [ ] **Step 4: Build and run desktop smoke**

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~HelloForm`

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- build examples/ui-hello-form --release --target win-x64`

Expected: headless test passes and a launchable desktop artifact is produced.

- [ ] **Step 5: Commit the example**

```powershell
git add examples/ui-hello-form tests/Vela.Ui.Runtime.Tests/HelloFormTests.cs
git commit -m "feat: add tested hello world ui form"
```

### Task 7: Validate platform targets and document UI development

**Files:**
- Create: `.github/workflows/ui-targets.yml`
- Create: `docs/articles/ui-getting-started.md`
- Create: `docs/articles/ui-components.md`
- Create: `docs/articles/ui-platforms.md`
- Modify: `docs/articles/toc.yml`, `docs/articles/examples.md`, `README.md`

- [ ] **Step 1: Add honest platform build jobs**

Create jobs for Windows App SDK and Skia Desktop on Windows, Skia Desktop on Linux/macOS, Android on a configured Android workload, iOS/Mac Catalyst on macOS/Xcode, and BrowserWasm. Cache NuGet/workloads by lock hash. Each job checks the Hello form and builds its own target; no job reports success after skipping a missing required workload.

- [ ] **Step 2: Run locally supported gates**

Run: `dotnet workload list`

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- build examples/ui-hello-form --release --target win-x64`

Run: `dotnet test tests/Vela.Ui.Runtime.Tests/Vela.Ui.Runtime.Tests.csproj --configuration Release`

Expected: Windows desktop build and all headless tests pass; unavailable Apple targets produce the documented host diagnostic.

- [ ] **Step 3: Document modules, accessibility, themes, forms, navigation, and target requirements**

Use only checked example source. Include `vela.ui.*` namespace ownership, third-party `publisher.ui.*` packages, target command matrix, workloads, Native AOT/platform AOT distinctions, escape-hatch restrictions, and accessibility checklist.

- [ ] **Step 4: Run full quality gates**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Run: `git diff --check`

Expected: every command exits 0.

- [ ] **Step 5: Commit UI CI and documentation**

```powershell
git add .github/workflows/ui-targets.yml docs/articles README.md
git commit -m "docs: add cross-platform vela ui guide"
```
