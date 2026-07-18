# Integration, Documentation, and Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate every approved subsystem, prove realistic applications and supply-chain flows, complete documentation/security/performance review, and publish the next unused SemVer release tag.

**Architecture:** Treat the preceding five plans as ordered prerequisites and add cross-subsystem tests only after each subsystem is independently green. Use checked examples as documentation sources, keep optional adapter size visible, and let tag publication occur only from a clean pushed commit after local and remote tag validation.

**Tech Stack:** .NET 10, Native AOT, Uno 6.5.36, ASP.NET Core, SQLite, PostgreSQL, Docker, DocFX, GitHub Actions, GitHub CLI.

---

## File map

- Create combined examples under `examples/apps/`.
- Create end-to-end tests under `tests/Vela.EndToEnd.Tests`.
- Extend benchmarks and size reporting.
- Update all user/contributor/release documentation.
- Update CI, docs, package-smoke, UI, registry, and release workflows.
- Add release verification scripts under `eng/release/verify-*`.

### Task 1: Add an end-to-end business API application

**Files:**
- Create: `examples/apps/inventory-api/vela.toml`
- Create: `examples/apps/inventory-api/src/main.vela`
- Create: `examples/apps/inventory-api/migrations/000000000001_create_inventory.sqlite.sql`
- Create: `tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj`
- Create: `tests/Vela.EndToEnd.Tests/InventoryApiTests.cs`
- Modify: `Vela.slnx`

- [ ] **Step 1: Write the failing HTTPS/SQLite behavior test**

```csharp
[Fact]
public async Task InventoryApiPersistsAndReturnsTypedItemsOverHttps()
{
    await using var app = await VelaAppFixture.BuildAndStartAsync("examples/apps/inventory-api");
    var created = await app.PostJsonAsync("/items", new { name = "Vela", quantity = 3 });
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var items = await app.GetJsonAsync<InventoryItem[]>("/items");
    Assert.Equal("Vela", Assert.Single(items).Name);
    Assert.Equal(3, items[0].Quantity);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryApi`

Expected: FAIL because the app/test project is absent.

- [ ] **Step 3: Implement the Vela application**

The app imports `vela.std.http.server`, `vela.std.http.openapi`, `vela.std.db`, and `vela.std.db.sqlite`; applies the migration; registers typed create/list/get routes; uses parameterized SQL; enforces 64 KiB bodies, 5-second route timeout, rate limiting, TLS, request IDs, structured logs, cancellation, and graceful shutdown. Test certificates and database paths come from the fixture environment.

- [ ] **Step 4: Run API and Native AOT smoke**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryApi`

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- build examples/apps/inventory-api --release --target win-x64`

Expected: test passes and Native AOT artifact is produced without warnings.

- [ ] **Step 5: Commit business API example**

```powershell
git add Vela.slnx examples/apps/inventory-api tests/Vela.EndToEnd.Tests
git commit -m "test: add inventory api end to end"
```

### Task 2: Prove remote third-party UI package consumption

**Files:**
- Create: `examples/packages/acme.ui.greeting/vela.toml`
- Create: `examples/packages/acme.ui.greeting/src/lib.vela`
- Create: `examples/apps/ui-remote-greeting/vela.toml`
- Create: `examples/apps/ui-remote-greeting/src/main.vela`
- Create: `tests/Vela.EndToEnd.Tests/RemoteUiPackageTests.cs`

- [ ] **Step 1: Write the failing publish/consume UI test**

```csharp
[Fact]
public async Task PublishesThirdPartyComponentAndBuildsConsumerFromLockfile()
{
    await using var registry = await VelaRegistryFixture.StartAsync();
    await registry.PublishWorkspaceAsync("examples/packages/acme.ui.greeting");
    await VelaCliFixture.RunAsync("add acme.ui.greeting@^1.0.0", "examples/apps/ui-remote-greeting", registry);
    await VelaCliFixture.RunAsync("build --release --target win-x64", "examples/apps/ui-remote-greeting", registry);
    Assert.Contains("acme.ui.greeting", File.ReadAllText("examples/apps/ui-remote-greeting/vela.lock"), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~RemoteUiPackage`

Expected: FAIL because the package and consumer dependency are absent.

- [ ] **Step 3: Add the third-party component without using `vela.ui.*` namespace**

`acme.ui.greeting` exports a source-linked `greeting_panel(message: State<Text>, output: State<Text>) -> UiNode` composed only from public official components. `ui-remote-greeting` consumes that component in a separate application; the exact approved `examples/ui-hello-form` source remains unchanged. The package manifest declares `uno-ui`, supported targets, license, repository, keywords, and category metadata.

- [ ] **Step 4: Run remote UI package integration**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~RemoteUiPackage`

Expected: PASS from a clean cache, then PASS in `--frozen` mode with the registry stopped.

- [ ] **Step 5: Commit package ecosystem proof**

```powershell
git add examples/packages/acme.ui.greeting examples/apps/ui-remote-greeting tests/Vela.EndToEnd.Tests/RemoteUiPackageTests.cs
git commit -m "test: consume remote third party ui package"
```

### Task 3: Add size, allocation, throughput, and ABI benchmarks

**Files:**
- Modify: `benchmarks/Vela.Runtime.Benchmarks/Program.cs`
- Create: `benchmarks/Vela.Runtime.Benchmarks/CallbackBenchmarks.cs`
- Create: `benchmarks/Vela.Runtime.Benchmarks/AbiBenchmarks.cs`
- Create: `benchmarks/Vela.Runtime.Benchmarks/RegistryCacheBenchmarks.cs`
- Create: `eng/measure-feature-sizes.ps1`
- Create: `tests/Vela.EndToEnd.Tests/PerformanceBudgetTests.cs`

- [ ] **Step 1: Write failing executable-size budget tests**

```csharp
[Fact]
public async Task BaseExecutableStaysWithinRuntimeBudget()
{
    var size = await FeatureSizeFixture.PublishAndMeasureAsync("base");
    Assert.True(size <= 3_145_728L, $"base size {size} exceeded 3145728");
}
```

- [ ] **Step 2: Run and capture the initial budget results**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~PerformanceBudget`

Expected before fixtures: FAIL; after fixtures, a concrete size report for every feature.

- [ ] **Step 3: Implement representative benchmarks and measured size report**

Benchmark cached non-capturing callbacks, captured callback invocation, text/buffer ABI round trip, handle retain/release, bulk collection transfer, route lookup, SQLite parameter query, registry ETag cache hit, and `.vlpkg` verification. `eng/measure-feature-sizes.ps1` publishes base/http/sqlite/postgres/ui examples, records executable/adapter/assets separately, and emits deterministic JSON.

- [ ] **Step 4: Run benchmarks and enforce only stable budgets**

Run: `dotnet run --project benchmarks/Vela.Runtime.Benchmarks/Vela.Runtime.Benchmarks.csproj -c Release -- --filter "*Callback*|*Abi*|*RegistryCache*"`

Run: `pwsh eng/measure-feature-sizes.ps1 -Configuration Release -Output artifacts/feature-sizes.json`

Expected: benchmark completes; the base limit remains enforced. Optional HTTP, SQLite, PostgreSQL, registry, and UI size deltas are reported and reviewed but are not assigned invented hard limits in this release.

- [ ] **Step 5: Commit performance gates**

```powershell
git add benchmarks eng/measure-feature-sizes.ps1 tests/Vela.EndToEnd.Tests/PerformanceBudgetTests.cs
git commit -m "perf: add feature and abi performance gates"
```

### Task 4: Perform security-focused tests and review

**Files:**
- Create: `tests/Vela.EndToEnd.Tests/SecurityBoundaryTests.cs`
- Create: `docs/articles/security-model.md`
- Modify: `docs/articles/toc.yml`

- [ ] **Step 1: Add boundary regression tests**

Cover HTTP body/header/timeouts, TLS downgrade, CORS default deny, SQL injection values, migration hash changes, ZIP traversal/decompression ratio, TUF rollback/freeze/mix-and-match, redirect credential leakage, package build-script rejection, token/log redaction, wrong-owner/stale ABI handles, callback-after-release, exception-to-status mapping, and native path hijacking.

- [ ] **Step 2: Run security tests**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~SecurityBoundary`

Expected: every attack fixture is rejected with its documented diagnostic/status and no partial external state.

- [ ] **Step 3: Review dependency and generated-project surfaces**

Run: `dotnet list Vela.slnx package --vulnerable --include-transitive`

Run: `dotnet list Vela.slnx package --deprecated`

Run: `rg -n "Process.Start|UseShellExecute|DllImport|LibraryImport|UnmanagedCallersOnly|Password|Token" src eng .github --glob '!**/bin/**' --glob '!**/obj/**'`

Expected: no known vulnerable packages; each native/process/secret call has an allowlisted, tested use.

- [ ] **Step 4: Document threat model and operational controls**

Document trust boundaries, assets, attackers, mitigations, residual risks, TLS/proxy setup, registry root recovery, token rotation, database backup/restore, ABI crash isolation limits, UI native escape hatch, and security contact/advisory flow.

- [ ] **Step 5: Commit security evidence**

```powershell
git add tests/Vela.EndToEnd.Tests/SecurityBoundaryTests.cs docs/articles/security-model.md docs/articles/toc.yml
git commit -m "test: harden new platform boundaries"
```

### Task 5: Complete user and API documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/index.md`
- Modify: `docs/articles/architecture.md`, `getting-started.md`, `language-tour.md`, `standard-library.md`, `examples.md`, `cli-reference.md`, `diagnostics.md`, `toc.yml`
- Modify: `docs/Vela-language.md`, `docs/Vela-collections.md`, `docs/toc.yml`
- Create: `docs/articles/abi-v2.md`, `http-server.md`, `database-access.md`, `ui-reference.md`, `registry-protocol.md`

- [ ] **Step 1: Generate a public API coverage inventory**

Run a documentation test that enumerates public Vela package declarations, core module bindings, CLI commands, diagnostics, ABI status codes, and registry endpoints, then asserts each stable identifier appears in one source documentation page.

- [ ] **Step 2: Run coverage and observe missing entries**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~DocumentationCoverage`

Expected initially: FAIL listing exact undocumented identifiers.

- [ ] **Step 3: Fill documentation from checked sources**

Document install, Hello UI form, API/data app, package publish/consume, modules, callbacks, ABI v2, C header, HTTP/TLS, SQLite/PostgreSQL/migrations, UI/accessibility/targets, registry/self-hosting/future website contract, cache/lock modes, diagnostics, security, performance, and release verification. Examples are copied from repository fixtures and checked by CI.

- [ ] **Step 4: Build docs with warnings as errors**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~DocumentationCoverage`

Run: `docfx docs/docfx.json --warningsAsErrors`

Expected: PASS with no broken internal links or undocumented stable identifiers.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md docs tests/Vela.EndToEnd.Tests
git commit -m "docs: document vela application platform"
```

### Task 6: Expand CI, package smoke, and release artifacts

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/package-smoke.yml`
- Modify: `.github/workflows/docs.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `eng/release/stage-runtime.ps1`
- Create: `eng/release/verify-release-assets.ps1`

- [ ] **Step 1: Add failing workflow/source validation assertions**

Add a repository test that parses every workflow and asserts: pinned action SHAs, least privilege, concurrency, timeouts, adapter staging, registry/UI matrices, required test dependencies, release `--repo "$GITHUB_REPOSITORY"`, checksum coverage, artifact attestations, and no secret interpolation into command lines.

- [ ] **Step 2: Run workflow validation**

Run: `dotnet test tests/Vela.EndToEnd.Tests/Vela.EndToEnd.Tests.csproj --configuration Release --filter FullyQualifiedName~WorkflowContract`

Expected initially: FAIL listing missing jobs/artifacts.

- [ ] **Step 3: Update workflows and release staging**

CI runs core tests on Windows/Linux/macOS, PostgreSQL/Docker integration on Linux, UI target workflow as defined in the UI plan, registry container integration, C header consumer builds, package restore offline/frozen tests, DocFX, dependency audit, and size gates. Package smoke stages all adapter projects/capability catalog/official packages/examples and builds unsigned installers. Release produces existing platform installers plus registry server/container metadata, capability catalog, official package bundle, ABI headers, checksums, attestations, and conditional signatures under the current approved unsigned-fallback policy.

- [ ] **Step 4: Run local workflow-equivalent gates**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Run: `pwsh eng/release/stage-runtime.ps1 -Destination artifacts/runtime-stage-smoke`

Run: `pwsh eng/release/verify-release-assets.ps1 -Directory artifacts/runtime-stage-smoke`

Expected: every command exits 0.

- [ ] **Step 5: Commit workflow expansion**

```powershell
git add .github/workflows eng/release tests/Vela.EndToEnd.Tests
git commit -m "ci: validate full vela platform release"
```

### Task 7: Final review, push, tag, and verify release

**Files:**
- Modify: `docs/releasing.md`
- Modify: release notes/changelog file selected by the existing project convention.

- [ ] **Step 1: Prove a clean reviewed release candidate**

Run: `git status --short`

Run: `git diff --check HEAD~1..HEAD`

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Expected: clean status and every gate exits 0. Review all new/changed code for correctness, security, disposal, cancellation, AOT/trimming, public API consistency, and English-only output.

- [ ] **Step 2: Determine and reserve the next unused SemVer tag**

```powershell
git fetch origin --tags --prune
$tags = (@(git tag --list 'v[0-9]*') + @(git ls-remote --tags --refs origin 'v[0-9]*' | ForEach-Object { ($_ -split "`t")[1] -replace '^refs/tags/', '' })) | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
$versions = $tags | Sort-Object -Unique | ForEach-Object { [version]($_ -replace '^v', '') }
$highest = $versions | Sort-Object -Descending | Select-Object -First 1
$releaseVersion = [version]::new($highest.Major, $highest.Minor + 1, 0)
$releaseTag = "v$releaseVersion"
if ($tags -contains $releaseTag) { throw "Release tag already exists: $releaseTag" }
$releaseTag
```

Expected with the current tag set: `v0.3.0`. Never delete or reuse an existing published tag.

- [ ] **Step 3: Update release notes and commit**

Document every user-visible feature, migration/compatibility rule, ABI v1/v2 behavior, target limitation, registry self-hosting status, known limitation, artifact verification command, and unsigned/signing status behavior. Then:

```powershell
git add docs/releasing.md CHANGELOG.md
git commit -m "docs: prepare v0.3.0 release"
```

If the repository has no `CHANGELOG.md`, create it with `v0.3.0` followed by Added, Changed, Security, Compatibility, and Known limitations sections populated from the implemented commits.

- [ ] **Step 4: Push the reviewed commit and create the annotated tag**

```powershell
git push origin master
git tag -a $releaseTag -m "Vela $releaseVersion"
git push origin $releaseTag
```

Expected: branch and tag pushes succeed; the tag targets the pushed release commit.

- [ ] **Step 5: Watch workflows and verify the published release**

```powershell
gh run list --repo stescobedo92/VelaMiniCompiler --limit 10
$runs = gh run list --repo stescobedo92/VelaMiniCompiler --branch $releaseTag --json databaseId,workflowName,status,conclusion | ConvertFrom-Json
if ($runs.Count -eq 0) { throw "No workflow runs found for $releaseTag" }
foreach ($run in $runs) { gh run watch $run.databaseId --repo stescobedo92/VelaMiniCompiler --exit-status }
gh release view $releaseTag --repo stescobedo92/VelaMiniCompiler --json tagName,url,isDraft,isPrerelease,assets
```

Expected: CI, docs, package smoke, UI/registry integration, and release runs are green; release is neither draft nor prerelease; `eng/release/verify-release-assets.ps1` succeeds against downloaded assets; checksums cover every final asset.
