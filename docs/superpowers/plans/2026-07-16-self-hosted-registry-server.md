# Self-Hosted Registry Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a secure, self-hosted Vela registry that implements the sparse client protocol and exposes all metadata required by a future crates.io-style website.

**Architecture:** Put low-level archive, signature, OIDC, and storage adapters in an opt-in AOT-compatible `Vela.Registry.Runtime` project. Write the executable route composition and business workflow in Vela using `vela.std.http.server` and `vela.std.db.postgres`. Store metadata/audit data in PostgreSQL and blobs behind a hash-addressed storage interface.

**Tech Stack:** Vela, .NET 10 Native AOT, ASP.NET Core/Kestrel, PostgreSQL/Npgsql, ECDSA P-256 TUF profile, OIDC/JWT, System.Text.Json source generation, xUnit, Docker.

---

## File map

- Create `src/Vela.Registry.Runtime` and `tests/Vela.Registry.Runtime.Tests`.
- Create `packages/vela.registry` source package.
- Create `apps/vela-registry/vela.toml`, migrations, and Vela source.
- Create `.github/workflows/registry-integration.yml`.
- Create container packaging under `eng/registry/`.
- Create registry API documentation and OpenAPI contract snapshots.

### Task 1: Freeze protocol and website-facing contracts

**Files:**
- Create: `src/Vela.Registry.Runtime/Vela.Registry.Runtime.csproj`
- Create: `src/Vela.Registry.Runtime/Contracts/RegistryContracts.cs`
- Create: `src/Vela.Registry.Runtime/Contracts/RegistryJsonContext.cs`
- Create: `tests/Vela.Registry.Runtime.Tests/ContractSerializationTests.cs`
- Create: `tests/Vela.Registry.Runtime.Tests/Snapshots/registry-openapi.json`
- Modify: `Vela.slnx`
- Modify: `eng/capabilities/vela-capabilities.json`

- [ ] **Step 1: Write failing stable-contract tests**

```csharp
[Fact]
public void PackageDetailContainsFutureWebsiteFieldsInStableOrder()
{
    var json = RegistryJson.Serialize(TestRegistryContracts.AcmeLog);
    Assert.Equal("""{"id":"pkg_acme_log","name":"acme.log","summary":"Structured logs","readme":"# acme.log","license":"MIT","repository":"https://example.test/acme/log","documentation":"https://docs.example.test/acme.log","keywords":["log"],"categories":["observability"],"owners":[{"id":"usr_1","name":"acme"}],"downloads":42,"recentDownloads":7,"latestVersion":"1.2.0"}""", json);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~ContractSerialization`

Expected: FAIL because registry contracts are absent.

- [ ] **Step 3: Define immutable API records and source-generated JSON**

```csharp
public sealed record RegistryPackageDetail(
    string Id,
    string Name,
    string Summary,
    string Readme,
    string License,
    Uri? Repository,
    Uri? Documentation,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Categories,
    IReadOnlyList<RegistryOwner> Owners,
    long Downloads,
    long RecentDownloads,
    string LatestVersion);

public sealed record RegistryPage<T>(IReadOnlyList<T> Items, string? NextCursor);
```

Add records for service discovery, sparse index entries, versions, dependencies, capabilities, ABI metadata, owners/organizations, audit events, publication receipts, search results, and advisories. JSON names and null behavior are explicit attributes; no reflection serializer fallback.

- [ ] **Step 4: Run contract tests**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~ContractSerialization`

Expected: PASS with deterministic snapshots.

- [ ] **Step 5: Commit registry contracts**

```powershell
git add Vela.slnx src/Vela.Registry.Runtime tests/Vela.Registry.Runtime.Tests eng/capabilities/vela-capabilities.json
git commit -m "feat: define registry service contracts"
```

### Task 2: Add PostgreSQL schema, repositories, and cursor search

**Files:**
- Create: `apps/vela-registry/migrations/*.postgres.sql`
- Create: `src/Vela.Registry.Runtime/Data/RegistryRepository.cs`
- Create: `src/Vela.Registry.Runtime/Data/RegistrySearchCursor.cs`
- Test: `tests/Vela.Registry.Runtime.Tests/RegistryRepositoryTests.cs`

- [ ] **Step 1: Write failing repository tests**

```csharp
[Fact]
public async Task PublishIsImmutableAndSearchUsesStableCursor()
{
    await using var fixture = await RegistryDatabaseFixture.StartAsync();
    await fixture.Repository.PublishAsync(TestPackages.AcmeLog100, CancellationToken.None);
    await Assert.ThrowsAsync<RegistryConflictException>(() => fixture.Repository.PublishAsync(TestPackages.AcmeLog100, CancellationToken.None));
    var first = await fixture.Repository.SearchAsync("log", RegistrySort.Downloads, 1, null, CancellationToken.None);
    var second = await fixture.Repository.SearchAsync("log", RegistrySort.Downloads, 1, first.NextCursor, CancellationToken.None);
    Assert.DoesNotContain(first.Items.Select(item => item.Id), id => second.Items.Any(item => item.Id == id));
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryRepository`

Expected: FAIL because schema/repository are absent.

- [ ] **Step 3: Create migration schema and parameterized repositories**

Migrations create users, organizations, memberships, namespaces, packages, versions, dependencies, owners, tokens, trusted publishers, audit events, download events/aggregates, moderation flags, and TUF role metadata. Enforce unique canonical name/version, immutable artifact/hash columns, owner foreign keys, and append-only audit rows. Cursors are base64url-encoded versioned JSON containing sort value plus package ID and are HMAC-authenticated with a configured server key.

- [ ] **Step 4: Run PostgreSQL repository tests**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryRepository`

Expected: PASS for ownership, immutable versions, yank/unyank, search sorts, pagination, dependency queries, and aggregate updates.

- [ ] **Step 5: Commit persistence**

```powershell
git add apps/vela-registry/migrations src/Vela.Registry.Runtime/Data tests/Vela.Registry.Runtime.Tests/RegistryRepositoryTests.cs
git commit -m "feat: persist registry metadata and search"
```

### Task 3: Implement safe content-addressed blob publication

**Files:**
- Create: `src/Vela.Registry.Runtime/Storage/IRegistryBlobStore.cs`
- Create: `src/Vela.Registry.Runtime/Storage/FileSystemRegistryBlobStore.cs`
- Create: `src/Vela.Registry.Runtime/Publishing/RegistryPublisher.cs`
- Test: `tests/Vela.Registry.Runtime.Tests/RegistryPublisherTests.cs`

- [ ] **Step 1: Write failing atomic publication tests**

```csharp
[Fact]
public async Task InvalidArchiveNeverCreatesVersionOrBlob()
{
    await using var fixture = await RegistryPublishFixture.CreateAsync();
    await Assert.ThrowsAsync<RegistryPackageException>(() => fixture.PublishAsync(TestArchives.PathTraversal));
    Assert.Empty(await fixture.Repository.ListVersionsAsync("acme.bad", CancellationToken.None));
    Assert.Empty(Directory.EnumerateFiles(fixture.BlobRoot, "*", SearchOption.AllDirectories));
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryPublisher`

Expected: FAIL because publication storage is absent.

- [ ] **Step 3: Implement staged validation and atomic commit**

```csharp
public interface IRegistryBlobStore
{
    ValueTask<Stream> OpenReadAsync(string sha256, CancellationToken cancellationToken);
    ValueTask PutVerifiedAsync(string sha256, Stream content, long length, CancellationToken cancellationToken);
    ValueTask<bool> ExistsAsync(string sha256, CancellationToken cancellationToken);
}
```

Stream upload to a temporary bounded file, compute server SHA-256, validate `.vlpkg` using the shared archive validator, then store by `<root>/sha256/<2>/<digest>`. Insert version metadata and publication audit in one database transaction only after blob verification; clean temp files on every failure. If database commit fails, retain an unreferenced content-addressed blob for safe later garbage collection.

- [ ] **Step 4: Run publisher tests**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryPublisher`

Expected: PASS for duplicate version, mismatched manifest, limits, concurrency, cleanup, and immutable downloads.

- [ ] **Step 5: Commit blob publication**

```powershell
git add src/Vela.Registry.Runtime/Storage src/Vela.Registry.Runtime/Publishing tests/Vela.Registry.Runtime.Tests/RegistryPublisherTests.cs
git commit -m "feat: publish immutable registry artifacts"
```

### Task 4: Add scoped tokens, OIDC trusted publishing, ownership, and audit

**Files:**
- Create: `src/Vela.Registry.Runtime/Auth/RegistryTokenService.cs`
- Create: `src/Vela.Registry.Runtime/Auth/RegistryOidcValidator.cs`
- Create: `src/Vela.Registry.Runtime/Auth/RegistryAuthorization.cs`
- Create: `src/Vela.Registry.Runtime/Audit/RegistryAuditWriter.cs`
- Test: `tests/Vela.Registry.Runtime.Tests/RegistryAuthenticationTests.cs`

- [ ] **Step 1: Write failing least-privilege tests**

```csharp
[Fact]
public async Task PackageScopedTokenCannotPublishAnotherNamespace()
{
    var token = await AuthFixture.IssueAsync(subject: "acme", scope: "publish:acme.*", expiresIn: TimeSpan.FromMinutes(5));
    Assert.True(await AuthFixture.AuthorizePublishAsync(token, "acme.log"));
    Assert.False(await AuthFixture.AuthorizePublishAsync(token, "other.log"));
}

[Fact]
public async Task OfficialPackageRequiresAllowlistedOidcWorkflow() =>
    Assert.False(await AuthFixture.AuthorizeOidcPublishAsync(TestOidc.UntrustedFork, "vela.ui"));
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryAuthentication`

Expected: FAIL because auth services are absent.

- [ ] **Step 3: Implement token hashes and exact OIDC claim matching**

Store random 256-bit token hashes using HMAC-SHA-256 with a server-side pepper; show token plaintext once. Validate issuer discovery/JWKS over HTTPS with bounded caching, signature, audience, expiry, not-before, repository, owner, ref, workflow reference, environment, and subject. Authorization intersects token scope, ownership, namespace reservation, and operation. Append audit events in the same transaction as every privileged state change.

- [ ] **Step 4: Run auth tests**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryAuthentication`

Expected: PASS for expired/revoked tokens, claim confusion, key rotation, wrong audience, fork PRs, ownership changes, and redacted logs.

- [ ] **Step 5: Commit registry identity controls**

```powershell
git add src/Vela.Registry.Runtime/Auth src/Vela.Registry.Runtime/Audit tests/Vela.Registry.Runtime.Tests/RegistryAuthenticationTests.cs
git commit -m "feat: secure registry publishing identities"
```

### Task 5: Generate and rotate TUF repository metadata

**Files:**
- Create: `src/Vela.Registry.Runtime/Trust/RegistryTufRepository.cs`
- Create: `src/Vela.Registry.Runtime/Trust/RegistrySigningKeyStore.cs`
- Test: `tests/Vela.Registry.Runtime.Tests/RegistryTufRepositoryTests.cs`

- [ ] **Step 1: Write failing metadata rotation tests**

```csharp
[Fact]
public async Task PublishUpdatesTargetsSnapshotTimestampAndPreservesOfflineRoot()
{
    await using var fixture = await RegistryTufFixture.CreateAsync();
    var before = await fixture.ReadVersionsAsync();
    await fixture.AddTargetAsync(TestPackages.AcmeLog100);
    var after = await fixture.ReadVersionsAsync();
    Assert.Equal(before.Root, after.Root);
    Assert.True(after.Targets > before.Targets);
    Assert.True(after.Snapshot > before.Snapshot);
    Assert.True(after.Timestamp > before.Timestamp);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryTufRepository`

Expected: FAIL because signing repository types are absent.

- [ ] **Step 3: Implement consistent signed metadata**

Keep threshold root keys offline and import only public root metadata into the server. Store encrypted online timestamp/snapshot/targets keys through a key-store interface. Update targets, then snapshot, then timestamp atomically with version increments and expirations; publish consistent-snapshot filenames by metadata hash. Package targets include digest, length, manifest hash, package/version, and publication receipt ID.

- [ ] **Step 4: Verify server metadata with the real client**

Run: `dotnet test tests/Vela.Registry.Runtime.Tests/Vela.Registry.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryTufRepository`

Run: `dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --configuration Release --filter FullyQualifiedName~TufClient`

Expected: server fixtures pass through the production client verifier, including online-key rotation and expired metadata recovery.

- [ ] **Step 5: Commit repository signing**

```powershell
git add src/Vela.Registry.Runtime/Trust tests/Vela.Registry.Runtime.Tests/RegistryTufRepositoryTests.cs tests/Vela.Packages.Tests
git commit -m "feat: sign registry repository metadata"
```

### Task 6: Compose the registry application in Vela

**Files:**
- Create: `packages/vela.registry/vela.toml`
- Create: `packages/vela.registry/src/lib.vela`
- Create: `apps/vela-registry/vela.toml`
- Create: `apps/vela-registry/src/main.vela`
- Create: `apps/vela-registry/vela.lock`
- Test: `tests/Vela.Backend.Tests/RegistryApplicationTests.cs`

- [ ] **Step 1: Write failing Vela application compile test**

```csharp
[Fact]
public void RegistryApplicationIsVelaSourceWithHttpAndPostgresCapabilities()
{
    var graph = VelaPackageResolver.Resolve(TestPaths.Repository("apps/vela-registry"), writeLockFile: false);
    var compilation = VelaCompiler.Compile(VelaSourceBundle.FromPackageGraph(graph));
    Assert.Empty(compilation.Diagnostics);
    Assert.Contains("aspnet-server", compilation.Capabilities);
    Assert.Contains("postgres", compilation.Capabilities);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~RegistryApplication --no-restore`

Expected: FAIL because the Vela package/application is absent.

- [ ] **Step 3: Implement explicit Vela routes**

`main.vela` configures HTTPS, PostgreSQL, migrations, limits, auth middleware, and graceful shutdown. Register service discovery, sparse index, metadata, downloads, search, package detail, versions, dependencies, owners, publish, yank/unyank, owner changes, audit-safe moderation, and OpenAPI routes. Route handlers call typed `vela.registry` operations; they never construct SQL, filesystem paths, JWT decisions, or signatures directly.

- [ ] **Step 4: Build and Native AOT publish the Vela app**

Run: `dotnet run --project src/Vela.Cli/Vela.Cli.csproj -- build apps/vela-registry --release --target win-x64`

Expected: exit 0 and a Native AOT registry executable with HTTP/PostgreSQL/registry adapters only.

- [ ] **Step 5: Commit the Vela registry app**

```powershell
git add packages/vela.registry apps/vela-registry tests/Vela.Backend.Tests/RegistryApplicationTests.cs
git commit -m "feat: compose registry server in vela"
```

### Task 7: Containerize and prove publish-consume-yank end to end

**Files:**
- Create: `eng/registry/Dockerfile`
- Create: `eng/registry/docker-compose.integration.yml`
- Create: `.github/workflows/registry-integration.yml`
- Create: `tests/Vela.Registry.Runtime.Tests/RegistryEndToEndTests.cs`
- Create: `docs/articles/registry-self-hosting.md`
- Modify: `docs/articles/toc.yml`

- [ ] **Step 1: Add the real container end-to-end test**

Start PostgreSQL and registry containers, initialize trusted root, publish two versions through scoped/OIDC fixtures, search them, resolve and compile a consumer through the production CLI, yank the selected version, restore the locked consumer, and assert audit/download counters. Use ephemeral certificates and keys created inside the test workspace.

- [ ] **Step 2: Run container integration**

Run: `docker compose -f eng/registry/docker-compose.integration.yml up --build --abort-on-container-exit --exit-code-from tests`

Expected: exit 0; containers are removed with `docker compose ... down --volumes` in `finally`/workflow `always()`.

- [ ] **Step 3: Document self-hosting and future website endpoints**

Document TLS/reverse-proxy trust, PostgreSQL, blob volume, migrations, offline root setup, online key rotation, OIDC publishers, backups, restore, metrics, health, all web-facing fields, stable cursors, and moderation/audit operations.

- [ ] **Step 4: Run release-quality gates**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Run: `git diff --check`

Expected: every command exits 0.

- [ ] **Step 5: Commit deployment and docs**

```powershell
git add eng/registry .github/workflows/registry-integration.yml tests/Vela.Registry.Runtime.Tests/RegistryEndToEndTests.cs docs/articles/registry-self-hosting.md docs/articles/toc.yml
git commit -m "feat: ship self-hosted vela registry"
```
