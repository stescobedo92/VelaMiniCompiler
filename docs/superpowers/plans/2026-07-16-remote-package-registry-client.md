# Remote Package Registry Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic SemVer resolution, `.vlpkg` artifacts, sparse remote registries, TUF-verified metadata, a content-addressed cache, lockfile modes, credentials, and package CLI commands.

**Architecture:** Introduce a dependency-light `Vela.Packages` project so package protocol, trust, caching, and resolution do not enlarge the runtime. Keep `VelaPackageResolver` as a compatibility facade that delegates to the new engine. Stream and verify every remote input before exposing it to the compiler.

**Tech Stack:** .NET 10, Tomlyn 2.10.1, `HttpClient`, `System.IO.Compression`, ECDSA P-256/SHA-256 TUF profile, System.Text.Json source generation, xUnit.

---

## File map

- Create `src/Vela.Packages/Vela.Packages.csproj`.
- Create `src/Vela.Packages/Manifests/VelaManifest.cs` and `VelaManifestReader.cs`.
- Create `src/Vela.Packages/Versions/VelaSemanticVersion.cs` and `VelaVersionRange.cs`.
- Create `src/Vela.Packages/Resolution/VelaDependencyResolver.cs`.
- Create `src/Vela.Packages/Locking/VelaLockDocument.cs` and `VelaLockFile.cs`.
- Create `src/Vela.Packages/Artifacts/VelaPackageArchive.cs`.
- Create `src/Vela.Packages/Caching/VelaPackageCache.cs`.
- Create `src/Vela.Packages/Trust/VelaTufClient.cs`, `VelaTufModels.cs`, and `VelaCanonicalJson.cs`.
- Create `src/Vela.Packages/Protocol/VelaRegistryClient.cs` and `VelaRegistryModels.cs`.
- Create `src/Vela.Packages/Credentials/IVelaCredentialStore.cs` and platform stores.
- Create `tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj` plus focused test files.
- Modify `Vela.slnx`, `src/Vela.Backend/Vela.Backend.csproj`, and `src/Vela.Backend/VelaPackageResolver.cs`.
- Create `src/Vela.Cli/PackageCommands.cs` and `src/Vela.Cli/RegistryCommands.cs`.
- Modify `src/Vela.Cli/Program.cs` and CLI help rendering.

### Task 1: Create the package project and parse real TOML/SemVer

**Files:**
- Create: `src/Vela.Packages/Vela.Packages.csproj`
- Create: `src/Vela.Packages/Manifests/VelaManifest.cs`
- Create: `src/Vela.Packages/Manifests/VelaManifestReader.cs`
- Create: `src/Vela.Packages/Versions/VelaSemanticVersion.cs`
- Create: `src/Vela.Packages/Versions/VelaVersionRange.cs`
- Create: `tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj`
- Create: `tests/Vela.Packages.Tests/ManifestAndVersionTests.cs`
- Modify: `Vela.slnx`

- [ ] **Step 1: Write failing manifest and SemVer tests**

```csharp
[Theory]
[InlineData("1.2.3", "1.2.3")]
[InlineData("1.2.3-beta.2+build.7", "1.2.3-beta.2+build.7")]
public void ParsesCanonicalSemVer(string text, string expected) =>
    Assert.Equal(expected, VelaSemanticVersion.Parse(text).ToString());

[Fact]
public void ReadsRemoteAndPathDependenciesWithoutRegexAmbiguity()
{
    var manifest = VelaManifestReader.Parse("""
        [package]
        name = "acme.app"
        version = "1.0.0"
        kind = "application"

        [dependencies]
        acme.json = "^2.1.0"
        acme.local = { path = "../local" }
        """, "C:/work/app/vela.toml");

    Assert.Equal(new VelaVersionRange("^2.1.0"), manifest.Dependencies["acme.json"].Version);
    Assert.Equal("../local", manifest.Dependencies["acme.local"].Path);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release`

Expected: FAIL because the project and types are absent.

- [ ] **Step 3: Create the project and exact immutable models**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Tomlyn" Version="2.10.1" />
  </ItemGroup>
</Project>
```

```csharp
public sealed record VelaManifest(
    string Name,
    VelaSemanticVersion Version,
    VelaPackageKind Kind,
    string ManifestPath,
    IReadOnlyDictionary<string, VelaDependencySpec> Dependencies,
    IReadOnlyList<string> Capabilities,
    VelaPackageMetadata Metadata);

public sealed record VelaDependencySpec(
    VelaVersionRange? Version,
    string? Path,
    string? Registry,
    bool Optional,
    IReadOnlyList<string> Features);
```

`VelaManifestReader` must parse through Tomlyn, reject unknown keys in security-sensitive tables, normalize package names, preserve line/column diagnostics, and require exactly one of path or version. `VelaSemanticVersion` implements SemVer 2.0 precedence; `VelaVersionRange` supports exact, comparison sets, caret, tilde, wildcard, and explicit prerelease selection.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release`

Expected: PASS for valid and invalid TOML/SemVer fixtures.

- [ ] **Step 5: Commit package parsing**

```powershell
git add Vela.slnx src/Vela.Packages tests/Vela.Packages.Tests
git commit -m "feat: parse package manifests and semver"
```

### Task 2: Implement deterministic dependency resolution and lockfiles

**Files:**
- Create: `src/Vela.Packages/Resolution/VelaDependencyResolver.cs`
- Create: `src/Vela.Packages/Resolution/IVelaPackageSource.cs`
- Create: `src/Vela.Packages/Locking/VelaLockDocument.cs`
- Create: `src/Vela.Packages/Locking/VelaLockFile.cs`
- Create: `tests/Vela.Packages.Tests/DependencyResolverTests.cs`
- Modify: `src/Vela.Backend/VelaPackageResolver.cs`
- Modify: `src/Vela.Backend/Vela.Backend.csproj`

- [ ] **Step 1: Write failing resolution tests**

```csharp
[Fact]
public async Task ResolverChoosesHighestCompatibleStableVersionDeterministically()
{
    var source = FakePackageSource.Create(
        ("acme.log", "1.2.0", false),
        ("acme.log", "1.3.0-beta.1", false),
        ("acme.log", "1.2.4", false),
        ("acme.log", "1.2.5", true));
    var result = await new VelaDependencyResolver([source]).ResolveAsync(
        TestManifests.Root(("acme.log", "^1.2.0")), VelaResolutionMode.Update, CancellationToken.None);
    Assert.Equal("1.2.4", Assert.Single(result.Packages).Version.ToString());
}

[Fact]
public async Task LockedModeRejectsAnyGraphChange()
{
    await Assert.ThrowsAsync<VelaLockException>(() => TestResolution.ResolveChangedGraph(VelaResolutionMode.Locked));
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~DependencyResolver`

Expected: FAIL because the resolver is absent.

- [ ] **Step 3: Implement deterministic backtracking and lock schema v2**

```csharp
public enum VelaResolutionMode { Update, Locked, Offline, Frozen }

public sealed record VelaLockedPackage(
    string Name,
    string Version,
    string Source,
    string ArtifactSha256,
    string ManifestSha256,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Capabilities,
    string? AbiContractHash);
```

Resolve package names ordinally, try compatible candidates in descending stable precedence, exclude yanked versions unless already locked, and detect cycles/conflicts with a dependency path in the exception. Serialize lock schema 2 as source-generated JSON with sorted packages/arrays and LF. `VelaPackageResolver.Resolve` delegates path-only graphs to the new engine and maps models back to existing public records.

- [ ] **Step 4: Run resolver and existing package tests**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~DependencyResolver`

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~Package --no-restore`

Expected: PASS; existing `vela.lock` fixtures remain readable and rewrite deterministically to schema 2 only when requested.

- [ ] **Step 5: Commit resolver and lockfile**

```powershell
git add src/Vela.Packages/Resolution src/Vela.Packages/Locking src/Vela.Backend/VelaPackageResolver.cs src/Vela.Backend/Vela.Backend.csproj tests/Vela.Packages.Tests/DependencyResolverTests.cs
git commit -m "feat: resolve and lock remote dependencies"
```

### Task 3: Build deterministic archives and the content-addressed cache

**Files:**
- Create: `src/Vela.Packages/Artifacts/VelaPackageArchive.cs`
- Create: `src/Vela.Packages/Artifacts/VelaArchiveLimits.cs`
- Create: `src/Vela.Packages/Caching/VelaPackageCache.cs`
- Create: `tests/Vela.Packages.Tests/PackageArchiveTests.cs`
- Create: `tests/Vela.Packages.Tests/PackageCacheTests.cs`

- [ ] **Step 1: Write failing reproducibility and traversal tests**

```csharp
[Fact]
public void PackagingSameTreeTwiceProducesIdenticalBytes()
{
    var first = VelaPackageArchive.Create(TestPackage.Root, TestPackage.Output("one.vlpkg"));
    var second = VelaPackageArchive.Create(TestPackage.Root, TestPackage.Output("two.vlpkg"));
    Assert.Equal(first.Sha256, second.Sha256);
    Assert.Equal(File.ReadAllBytes(first.Path), File.ReadAllBytes(second.Path));
}

[Theory]
[InlineData("../escape")]
[InlineData("/absolute")]
[InlineData("CON")]
public void ExtractionRejectsUnsafeEntry(string name) =>
    Assert.Throws<VelaPackageArchiveException>(() => TestArchives.ExtractSingleEntry(name));
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter "FullyQualifiedName~PackageArchive|FullyQualifiedName~PackageCache"`

Expected: FAIL because archive/cache types are absent.

- [ ] **Step 3: Implement deterministic ZIP and atomic cache writes**

```csharp
public sealed record VelaArchiveLimits(
    long MaxCompressedBytes = 32 * 1024 * 1024,
    long MaxExpandedBytes = 128 * 1024 * 1024,
    int MaxEntries = 4096,
    int MaxPathLength = 240);

public sealed record VelaPackageArtifact(string Path, string Sha256, long Length);
```

Use `ZipArchive` with forward-slash UTF-8 names, ordinal entry order, `1980-01-01T00:00:00Z`, no symlinks, and no duplicate canonical paths. Stream extraction while enforcing compressed size, expanded size, entry count, ratio, path length, root containment, and SHA-256. Cache under `<cache>/objects/sha256/<first-two>/<digest>` using a unique temp file, `FileMode.CreateNew`, per-digest lock file, verified atomic move, and read-only extracted trees.

- [ ] **Step 4: Run archive tests repeatedly**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter "FullyQualifiedName~PackageArchive|FullyQualifiedName~PackageCache"`

Expected: PASS including concurrent writers and malicious ZIP fixtures.

- [ ] **Step 5: Commit artifacts and cache**

```powershell
git add src/Vela.Packages/Artifacts src/Vela.Packages/Caching tests/Vela.Packages.Tests/PackageArchiveTests.cs tests/Vela.Packages.Tests/PackageCacheTests.cs
git commit -m "feat: add deterministic package artifacts and cache"
```

### Task 4: Implement the Vela TUF profile

**Files:**
- Create: `src/Vela.Packages/Trust/VelaCanonicalJson.cs`
- Create: `src/Vela.Packages/Trust/VelaTufModels.cs`
- Create: `src/Vela.Packages/Trust/VelaTufClient.cs`
- Create: `src/Vela.Packages/Trust/VelaTufCrypto.cs`
- Create: `tests/Vela.Packages.Tests/TufClientTests.cs`
- Create: `tests/Vela.Packages.Tests/Fixtures/tuf/**`

- [ ] **Step 1: Write failing TUF chain and attack tests**

```csharp
[Fact]
public async Task VerifiesRootTimestampSnapshotTargetsAndArtifact()
{
    var client = TestTuf.CreateClient("valid-chain");
    var target = await client.RefreshAndGetTargetAsync("packages/acme.log/1.2.4.vlpkg", CancellationToken.None);
    Assert.Equal(TestTuf.AcmeLogSha256, target.Sha256);
}

[Theory]
[InlineData("expired-timestamp")]
[InlineData("rollback-snapshot")]
[InlineData("mixed-targets")]
[InlineData("insufficient-root-threshold")]
public async Task RejectsKnownUpdateAttacks(string fixture) =>
    await Assert.ThrowsAsync<VelaTrustException>(() => TestTuf.CreateClient(fixture).RefreshAsync(CancellationToken.None));
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~TufClient`

Expected: FAIL because trust types are absent.

- [ ] **Step 3: Implement the fixed TUF 1.0 profile**

```csharp
public sealed record VelaTufKey(string KeyId, string Scheme, string PublicKeyPem);
public sealed record VelaTufRole(IReadOnlyList<string> KeyIds, int Threshold);
public sealed record VelaTufTarget(long Length, IReadOnlyDictionary<string, string> Hashes, JsonElement Custom);
```

Use ECDSA P-256 with SHA-256 through `System.Security.Cryptography.ECDsa`; canonical JSON uses UTF-8, sorted object keys, no insignificant whitespace, and integer-only numbers. Implement the TUF client workflow in order: trusted root, sequential root rotation, timestamp, snapshot, targets, artifact. Persist the highest trusted metadata versions per registry and reject rollback, expiry, hash/length mismatch, mix-and-match, unknown schemes, and insufficient thresholds. Fixtures include offline root keys and online role keys generated only for tests.

- [ ] **Step 4: Run trust tests**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~TufClient`

Expected: PASS for the valid chain and every attack fixture.

- [ ] **Step 5: Commit trust verification**

```powershell
git add src/Vela.Packages/Trust tests/Vela.Packages.Tests/TufClientTests.cs tests/Vela.Packages.Tests/Fixtures/tuf
git commit -m "feat: verify signed registry metadata"
```

### Task 5: Add sparse registry protocol, credentials, and package source

**Files:**
- Create: `src/Vela.Packages/Protocol/VelaRegistryModels.cs`
- Create: `src/Vela.Packages/Protocol/VelaRegistryClient.cs`
- Create: `src/Vela.Packages/Protocol/VelaRemotePackageSource.cs`
- Create: `src/Vela.Packages/Credentials/IVelaCredentialStore.cs`
- Create: `src/Vela.Packages/Credentials/VelaCredentialStore.cs`
- Create: `tests/Vela.Packages.Tests/RegistryClientTests.cs`

- [ ] **Step 1: Write failing sparse/ETag/auth tests**

```csharp
[Fact]
public async Task SparseClientUsesConditionalRequestAndVerifiedArtifact()
{
    await using var registry = await TestRegistry.StartAsync();
    var client = registry.CreateClient();
    await client.GetPackageVersionsAsync("acme.log", CancellationToken.None);
    await client.GetPackageVersionsAsync("acme.log", CancellationToken.None);
    Assert.Equal("\"acme-log-v1\"", registry.LastIfNoneMatch);
    Assert.Equal(1, registry.PackageIndexBodyResponses);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryClient`

Expected: FAIL because registry protocol types are absent.

- [ ] **Step 3: Implement bounded HTTP and credential providers**

```csharp
public sealed record VelaRegistryService(
    int ProtocolVersion,
    string RegistryId,
    Uri Api,
    Uri Index,
    Uri Download,
    Uri Metadata,
    long MaxPackageBytes,
    bool AuthenticationRequired);
```

Use one injected `HttpClient`, HTTPS-only except loopback development, 10-second metadata timeout, streaming downloads, 4 MiB metadata limit, `ETag`, cancellation, and no automatic credential forwarding across host redirects. `IVelaCredentialStore` supports environment tokens and OS stores: Windows Credential Manager P/Invoke, macOS `security` with `ProcessStartInfo.ArgumentList`, Linux `secret-tool` with `ArgumentList`; when the OS provider is absent, `login` fails safely unless `--no-save` is used.

- [ ] **Step 4: Run protocol tests**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryClient`

Expected: PASS for ETag, 304, auth challenge, redirect rejection, timeout, oversized metadata, TUF verification, and atomic artifact cache.

- [ ] **Step 5: Commit registry client**

```powershell
git add src/Vela.Packages/Protocol src/Vela.Packages/Credentials tests/Vela.Packages.Tests/RegistryClientTests.cs
git commit -m "feat: add secure sparse registry client"
```

### Task 6: Expose registry and package CLI commands

**Files:**
- Create: `src/Vela.Cli/PackageCommands.cs`
- Create: `src/Vela.Cli/RegistryCommands.cs`
- Modify: `src/Vela.Cli/Program.cs`
- Modify: `src/Vela.Cli/Vela.Cli.csproj`
- Create: `tests/Vela.Cli.Tests/Vela.Cli.Tests.csproj`
- Create: `tests/Vela.Cli.Tests/PackageCommandTests.cs`
- Modify: `Vela.slnx`

- [ ] **Step 1: Write failing command tests**

```csharp
[Theory]
[InlineData("registry list")]
[InlineData("search acme --json")]
[InlineData("add acme.log@^1.2.0 --registry private")]
[InlineData("update --locked")]
[InlineData("package")]
[InlineData("publish --registry private")]
[InlineData("yank acme.log@1.2.4")]
[InlineData("owner list acme.log")]
public async Task ParsesPackageCommandsWithoutTreatingSecretsAsArguments(string command)
{
    var result = await CliTestHost.RunAsync(command);
    Assert.DoesNotContain("Unknown command", result.StandardError, StringComparison.Ordinal);
    Assert.DoesNotContain("token", result.RenderedCommand, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Cli.Tests/Vela.Cli.Tests.csproj --configuration Release`

Expected: FAIL because package commands and the CLI test project are absent.

- [ ] **Step 3: Implement command dispatch with injectable services**

```csharp
internal sealed record VelaCliServices(
    IVelaRegistryClientFactory Registries,
    IVelaCredentialStore Credentials,
    VelaPackageCache Cache,
    TimeProvider TimeProvider);
```

Move package/registry parsing and execution out of `Program.cs`. Add `--registry`, `--json`, `--offline`, `--locked`, `--frozen`, `--no-save`, and cancellation. `publish` packages first, displays name/version/digest/registry, and requires explicit non-interactive confirmation through `--yes`. JSON output is source-generated and stable. Never accept tokens on the command line.

- [ ] **Step 4: Run CLI and solution tests**

Run: `dotnet test tests/Vela.Cli.Tests/Vela.Cli.Tests.csproj --configuration Release`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Expected: PASS; legacy check/run/build behavior remains unchanged.

- [ ] **Step 5: Commit CLI commands**

```powershell
git add Vela.slnx src/Vela.Cli tests/Vela.Cli.Tests
git commit -m "feat: add remote package commands"
```

### Task 7: End-to-end restore and documentation gate

**Files:**
- Create: `tests/Vela.Packages.Tests/RemoteRestoreIntegrationTests.cs`
- Modify: `docs/articles/cli-reference.md`
- Modify: `docs/articles/standard-library.md`
- Create: `docs/articles/package-registry.md`
- Modify: `docs/articles/toc.yml`

- [ ] **Step 1: Add the publish-resolve-lock-restore integration test**

The test starts the in-process HTTPS registry fixture, publishes `acme.log` 1.0.0 and 1.1.0, resolves `^1.0.0`, writes a lockfile, deletes the workspace package tree, restores from the verified cache in frozen mode, yanks 1.1.0, and proves the existing lock still restores while a new unlocked resolution chooses 1.0.0.

- [ ] **Step 2: Run the integration test**

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~RemoteRestoreIntegration`

Expected: PASS with no external network access.

- [ ] **Step 3: Document exact commands and trust behavior**

Document registry add/login, search/add/update/package/publish/yank/owner, SemVer, lock modes, cache paths, TUF fingerprint confirmation, OIDC trusted publishing, private registries, and recovery from expired metadata. Examples must use the integration fixture command shapes.

- [ ] **Step 4: Run full gates**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Run: `git diff --check`

Expected: every command exits 0.

- [ ] **Step 5: Commit registry client documentation**

```powershell
git add tests/Vela.Packages.Tests/RemoteRestoreIntegrationTests.cs docs/articles/cli-reference.md docs/articles/standard-library.md docs/articles/package-registry.md docs/articles/toc.yml
git commit -m "docs: document remote package registries"
```
