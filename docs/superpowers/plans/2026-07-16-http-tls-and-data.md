# HTTP, TLS, and Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide opt-in structured HTTP/HTTPS client/server packages plus explicit, asynchronous SQLite/PostgreSQL access and versioned migrations.

**Architecture:** Keep framework-heavy code in separate adapter projects selected by capabilities, not in `Vela.Runtime`. Expose reflection-free host types to generated Vela code and source-linked `vela.std.*` facades. Stream HTTP bodies and query rows by default; make limits, cancellation, transactions, and ownership explicit.

**Tech Stack:** ASP.NET Core/Kestrel .NET 10, System.Text.Json source generation, Microsoft.Data.Sqlite 10.0.10, Npgsql 10.0.3, Testcontainers.PostgreSql 4.13.0, Native AOT, xUnit.

---

## File map

- Create `src/Vela.Http.Runtime` and `tests/Vela.Http.Runtime.Tests`.
- Create `src/Vela.Data.Runtime` and `tests/Vela.Data.Runtime.Tests`.
- Create `src/Vela.Sqlite.Runtime` and `tests/Vela.Sqlite.Runtime.Tests`.
- Create `src/Vela.Postgres.Runtime` and `tests/Vela.Postgres.Runtime.Tests`.
- Create source packages `packages/vela.std.http.server`, `packages/vela.std.http.openapi`, `packages/vela.std.db`, `packages/vela.std.db.sqlite`, and `packages/vela.std.db.postgres`.
- Expand `packages/vela.std.http` to structured TLS client operations while retaining compatibility helpers.
- Modify capability catalog, build staging, solution, tests, docs, and CI.

### Task 1: Create isolated adapter projects and capability staging

**Files:**
- Create: `src/Vela.Http.Runtime/Vela.Http.Runtime.csproj`
- Create: `src/Vela.Data.Runtime/Vela.Data.Runtime.csproj`
- Create: `src/Vela.Sqlite.Runtime/Vela.Sqlite.Runtime.csproj`
- Create: `src/Vela.Postgres.Runtime/Vela.Postgres.Runtime.csproj`
- Create matching test projects under `tests/`
- Modify: `Vela.slnx`
- Modify: `eng/capabilities/vela-capabilities.json`
- Modify: `eng/release/stage-runtime.ps1`
- Modify: `src/Vela.Backend/VelaBuildService.cs`

- [ ] **Step 1: Write a failing generated-project isolation test**

```csharp
[Fact]
public void GeneratedProjectAddsOnlySelectedAdapterProjects()
{
    var sqlite = TestBuild.WriteProject(["sqlite"]);
    Assert.Contains("Vela.Sqlite.Runtime.csproj", File.ReadAllText(sqlite.ProjectPath), StringComparison.Ordinal);
    Assert.DoesNotContain("Vela.Postgres.Runtime.csproj", File.ReadAllText(sqlite.ProjectPath), StringComparison.Ordinal);
    Assert.DoesNotContain("Vela.Http.Runtime.csproj", File.ReadAllText(sqlite.ProjectPath), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the test and verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~GeneratedProjectAddsOnlySelectedAdapterProjects --no-restore`

Expected: FAIL because adapter projects are absent.

- [ ] **Step 3: Create exact project boundaries**

```xml
<!-- Vela.Http.Runtime.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../Vela.Runtime/Vela.Runtime.csproj" /></ItemGroup>
</Project>
```

```xml
<!-- Vela.Sqlite.Runtime.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.10" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../Vela.Data.Runtime/Vela.Data.Runtime.csproj" /></ItemGroup>
</Project>
```

```xml
<!-- Vela.Postgres.Runtime.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="Npgsql" Version="10.0.3" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../Vela.Data.Runtime/Vela.Data.Runtime.csproj" /></ItemGroup>
</Project>
```

Set `net10.0`, nullable, implicit usings, `IsAotCompatible`, documentation, and warnings-as-errors through existing common props. Stage each project and source folder under installed `runtime/adapters/<capability>`; generated project references the canonical staged path selected by the catalog.

- [ ] **Step 4: Build isolated projects and run the focused test**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter FullyQualifiedName~GeneratedProjectAddsOnlySelectedAdapterProjects --no-restore`

Expected: PASS and base generated projects reference no optional adapters.

- [ ] **Step 5: Commit adapter scaffolding**

```powershell
git add Vela.slnx src/Vela.Http.Runtime src/Vela.Data.Runtime src/Vela.Sqlite.Runtime src/Vela.Postgres.Runtime tests/Vela.Http.Runtime.Tests tests/Vela.Data.Runtime.Tests tests/Vela.Sqlite.Runtime.Tests tests/Vela.Postgres.Runtime.Tests eng/capabilities/vela-capabilities.json eng/release/stage-runtime.ps1 src/Vela.Backend/VelaBuildService.cs
git commit -m "feat: isolate http and data adapters"
```

### Task 2: Implement structured HTTP client and bounded server routing

**Files:**
- Create: `src/Vela.Http.Runtime/VelaHttpRequest.cs`
- Create: `src/Vela.Http.Runtime/VelaHttpResponse.cs`
- Create: `src/Vela.Http.Runtime/VelaHttpClient.cs`
- Create: `src/Vela.Http.Runtime/VelaHttpServer.cs`
- Create: `src/Vela.Http.Runtime/VelaHttpLimits.cs`
- Test: `tests/Vela.Http.Runtime.Tests/HttpClientServerTests.cs`

- [ ] **Step 1: Write failing loopback and limit tests**

```csharp
[Fact]
public async Task RoutesTypedRequestAndStreamsBoundedResponse()
{
    await using var server = VelaHttpServer.Create("test", new VelaHttpLimits(MaxBodyBytes: 1024));
    server.Map("POST", "/echo/{id:int}", static async (request, token) =>
        VelaHttpResponse.Json(200, new EchoResponse(request.RouteInt("id"), await request.ReadTextAsync(token))));
    await server.StartLoopbackAsync();
    var response = await VelaHttpClient.Shared.SendAsync(VelaHttpRequest.Post(server.Uri("/echo/7"), "hello"), CancellationToken.None);
    Assert.Equal(200, response.StatusCode);
    Assert.Contains("hello", await response.ReadTextAsync(CancellationToken.None), StringComparison.Ordinal);
}

[Fact]
public async Task RejectsOversizedBodyBeforeHandlerRuns() =>
    Assert.Equal(413, await HttpFixture.SendBodyAsync(configuredLimit: 4, body: "12345"));
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Http.Runtime.Tests/Vela.Http.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~HttpClientServer`

Expected: FAIL because HTTP runtime types are absent.

- [ ] **Step 3: Implement immutable request/response and static route registration**

```csharp
public sealed record VelaHttpLimits(
    long MaxBodyBytes = 1_048_576,
    int MaxHeaderCount = 64,
    int MaxConcurrentRequests = 256,
    int QueueLimit = 0,
    TimeSpan? RequestTimeout = null);

public delegate ValueTask<VelaHttpResponse> VelaHttpHandler(VelaHttpRequest request, CancellationToken cancellationToken);
```

Use `SocketsHttpHandler` with decompression and redirects disabled by default. Kestrel routes are registered explicitly through `MapMethods`; duplicate method/template pairs fail during build. Wrap `HttpRequest.BodyReader` and `HttpResponse.BodyWriter`; never use unbounded `ReadToEnd`. Use generated JSON type info passed by the caller, not reflection.

- [ ] **Step 4: Run HTTP tests**

Run: `dotnet test tests/Vela.Http.Runtime.Tests/Vela.Http.Runtime.Tests.csproj --configuration Release`

Expected: PASS for routes, malformed route values, headers, streaming, cancellation, timeouts, concurrency, 404/405, and limits.

- [ ] **Step 5: Commit HTTP core**

```powershell
git add src/Vela.Http.Runtime tests/Vela.Http.Runtime.Tests
git commit -m "feat: add bounded structured http runtime"
```

### Task 3: Add production TLS, middleware, graceful shutdown, and OpenAPI

**Files:**
- Create: `src/Vela.Http.Runtime/VelaTlsOptions.cs`
- Create: `src/Vela.Http.Runtime/VelaHttpMiddleware.cs`
- Create: `src/Vela.Http.Runtime/VelaOpenApiDocument.cs`
- Test: `tests/Vela.Http.Runtime.Tests/TlsAndMiddlewareTests.cs`

- [ ] **Step 1: Write failing TLS/security tests**

```csharp
[Fact]
public async Task HttpsUsesTls12OrLaterAndConfiguredCertificate()
{
    await using var fixture = await HttpsFixture.StartAsync();
    var result = await fixture.ConnectAsync();
    Assert.True(result.Protocol is SslProtocols.Tls12 or SslProtocols.Tls13);
    Assert.Equal(fixture.Certificate.Thumbprint, result.RemoteCertificateThumbprint);
}

[Fact]
public void ProductionRejectsDevelopmentCertificate() =>
    Assert.Throws<VelaHttpConfigurationException>(() => HttpsFixture.CreateProductionWithDevelopmentCertificate());
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Http.Runtime.Tests/Vela.Http.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~TlsAndMiddleware`

Expected: FAIL because TLS/middleware types are absent.

- [ ] **Step 3: Implement secure defaults and deterministic OpenAPI**

```csharp
public sealed record VelaTlsOptions(
    string? PfxPath,
    string? PemCertificatePath,
    string? PemKeyPath,
    string? StoreThumbprint,
    string? PasswordEnvironmentVariable,
    SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13);
```

Load passwords only from the named environment variable/provider. Add ordered middleware for request ID, exception mapping, structured access logs, CORS, rate limiting, authentication/authorization hooks, compression, and timeout. CORS has no allowed origin by default. Shutdown calls `StopAsync(gracePeriod)` then disposes handlers. Generate OpenAPI from route descriptors sorted by path/method and source-generated JSON.

- [ ] **Step 4: Run TLS and Native AOT publish**

Run: `dotnet test tests/Vela.Http.Runtime.Tests/Vela.Http.Runtime.Tests.csproj --configuration Release`

Run: `dotnet publish tests/Vela.Http.Runtime.Tests/Fixtures/AotServer/AotServer.csproj -c Release -r win-x64`

Expected: tests pass and Native AOT publish completes without trim/AOT warnings.

- [ ] **Step 5: Commit secure HTTP hosting**

```powershell
git add src/Vela.Http.Runtime tests/Vela.Http.Runtime.Tests
git commit -m "feat: secure http hosting with tls"
```

### Task 4: Define provider-neutral data contracts and SQLite

**Files:**
- Create: `src/Vela.Data.Runtime/VelaDbValue.cs`
- Create: `src/Vela.Data.Runtime/VelaDataContracts.cs`
- Create: `src/Vela.Data.Runtime/VelaDbException.cs`
- Create: `src/Vela.Sqlite.Runtime/VelaSqliteDataSource.cs`
- Test: `tests/Vela.Sqlite.Runtime.Tests/SqliteDataSourceTests.cs`

- [ ] **Step 1: Write failing SQLite parameter/transaction/stream tests**

```csharp
[Fact]
public async Task ParameterizedQueryStreamsRowsAndRollsBackUncommittedTransaction()
{
    await using var source = VelaSqliteDataSource.Open("Data Source=:memory:");
    await source.ExecuteAsync("CREATE TABLE message(id INTEGER PRIMARY KEY, text TEXT NOT NULL)", [], CancellationToken.None);
    await using (var transaction = await source.BeginTransactionAsync(CancellationToken.None))
    {
        await transaction.ExecuteAsync("INSERT INTO message(text) VALUES ($text)", [VelaDbParameter.Text("$text", "hello")], CancellationToken.None);
    }
    Assert.Equal(0L, await source.ScalarInt64Async("SELECT COUNT(*) FROM message", [], CancellationToken.None));
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Sqlite.Runtime.Tests/Vela.Sqlite.Runtime.Tests.csproj --configuration Release`

Expected: FAIL because data contracts/providers are absent.

- [ ] **Step 3: Implement explicit values, readers, disposal, and SQLite options**

```csharp
public enum VelaDbValueKind { Null, Bool, Int32, UInt32, Int64, Float, Double, Decimal, Text, Bytes, Uuid, DateTime, Json }
public readonly record struct VelaDbParameter(string Name, VelaDbValue Value);

public interface IVelaRowReader : IAsyncDisposable
{
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
    VelaDbValue Get(int ordinal);
    int GetOrdinal(string name);
}
```

SQLite uses named parameters, typed `SqliteParameter`, foreign keys enabled, configurable busy timeout/read-only/WAL, and deterministic async disposal. `CollectAsync(maxRows)` throws before adding row `maxRows + 1`. Transactions rollback on disposal unless committed and reject calls after commit/rollback.

- [ ] **Step 4: Run SQLite tests and AOT publish fixture**

Run: `dotnet test tests/Vela.Sqlite.Runtime.Tests/Vela.Sqlite.Runtime.Tests.csproj --configuration Release`

Run: `dotnet publish tests/Vela.Sqlite.Runtime.Tests/Fixtures/AotSqlite/AotSqlite.csproj -c Release -r win-x64`

Expected: PASS with parameter injection fixtures stored as literal text.

- [ ] **Step 5: Commit SQLite support**

```powershell
git add src/Vela.Data.Runtime src/Vela.Sqlite.Runtime tests/Vela.Data.Runtime.Tests tests/Vela.Sqlite.Runtime.Tests
git commit -m "feat: add explicit sqlite data access"
```

### Task 5: Implement pooled PostgreSQL with slim Native AOT configuration

**Files:**
- Create: `src/Vela.Postgres.Runtime/VelaPostgresDataSource.cs`
- Create: `src/Vela.Postgres.Runtime/VelaPostgresOptions.cs`
- Test: `tests/Vela.Postgres.Runtime.Tests/PostgresDataSourceTests.cs`
- Modify: `tests/Vela.Postgres.Runtime.Tests/Vela.Postgres.Runtime.Tests.csproj`

- [ ] **Step 1: Write failing container integration tests**

```csharp
[Fact]
public async Task UsesPositionalParametersPoolingStreamingAndCancellation()
{
    await using var database = await PostgresFixture.StartAsync();
    await using var source = VelaPostgresDataSource.Create(database.ConnectionString);
    await source.ExecuteAsync("CREATE TABLE item(id integer, name text)", [], CancellationToken.None);
    await source.ExecuteAsync("INSERT INTO item VALUES ($1, $2)", [VelaDbParameter.Int32(7), VelaDbParameter.Text("safe'); DROP TABLE item;--")], CancellationToken.None);
    await using var reader = await source.QueryAsync("SELECT name FROM item WHERE id=$1", [VelaDbParameter.Int32(7)], CancellationToken.None);
    Assert.True(await reader.ReadAsync(CancellationToken.None));
    Assert.Contains("DROP TABLE", reader.Get(0).AsText(), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Postgres.Runtime.Tests/Vela.Postgres.Runtime.Tests.csproj --configuration Release`

Expected: FAIL because the provider is absent.

- [ ] **Step 3: Implement one slim data source and short-lived connections**

Use `NpgsqlSlimDataSourceBuilder`, positional placeholders only, strongly typed `NpgsqlParameter<T>` for value types, pooling enabled by default, bounded min/max pool size, command timeout, TLS modes, cancellation tokens, and `await using`. Transactions pin one connection and optionally expose named savepoints; nested transaction creation is rejected.

```csharp
public sealed record VelaPostgresOptions(
    string ConnectionString,
    int MinimumPoolSize = 0,
    int MaximumPoolSize = 100,
    TimeSpan CommandTimeout = default,
    bool RequireTls = true);
```

- [ ] **Step 4: Run PostgreSQL tests and AOT fixture**

Run: `dotnet test tests/Vela.Postgres.Runtime.Tests/Vela.Postgres.Runtime.Tests.csproj --configuration Release`

Run on the current Windows execution host: `dotnet publish tests/Vela.Postgres.Runtime.Tests/Fixtures/AotPostgres/AotPostgres.csproj -c Release -r win-x64`

Expected: integration tests and the Windows Native AOT fixture pass; Linux CI publishes the same fixture for `linux-x64`. Missing Docker is an infrastructure failure, not a skipped green test.

- [ ] **Step 5: Commit PostgreSQL support**

```powershell
git add src/Vela.Postgres.Runtime tests/Vela.Postgres.Runtime.Tests
git commit -m "feat: add pooled postgresql data access"
```

### Task 6: Add validated migration runner

**Files:**
- Create: `src/Vela.Data.Runtime/Migrations/VelaMigration.cs`
- Create: `src/Vela.Data.Runtime/Migrations/VelaMigrationRunner.cs`
- Create: `src/Vela.Cli/MigrationCommands.cs`
- Test: `tests/Vela.Data.Runtime.Tests/MigrationRunnerTests.cs`

- [ ] **Step 1: Write failing order/hash/rollback tests**

```csharp
[Fact]
public async Task AppliesInOrderAndRejectsChangedAppliedHash()
{
    var runner = MigrationFixture.Create("001_create.sql", "002_seed.sql");
    await runner.ApplyAsync(CancellationToken.None);
    MigrationFixture.Change("001_create.sql", "SELECT 2;");
    await Assert.ThrowsAsync<VelaMigrationException>(() => runner.ValidateAsync(CancellationToken.None));
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Vela.Data.Runtime.Tests/Vela.Data.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~Migration`

Expected: FAIL because migration types are absent.

- [ ] **Step 3: Implement exact migration state machine**

```csharp
public sealed record VelaMigration(long Id, string Description, string Provider, string Sha256, string Sql);
public sealed record VelaAppliedMigration(long Id, string Description, string Sha256, DateTimeOffset AppliedAt);
```

Parse `<12-digit-id>_<slug>.<sqlite|postgres>.sql`, reject duplicates/non-monotonic IDs, compute SHA-256 over normalized UTF-8/LF, acquire provider lock, create `_vela_migrations`, validate applied hashes, apply pending migrations transactionally, and record success in the same transaction. Add CLI `migration create|status|validate|apply` with no implicit production connection string logging.

- [ ] **Step 4: Run data and CLI tests**

Run: `dotnet test tests/Vela.Data.Runtime.Tests/Vela.Data.Runtime.Tests.csproj --configuration Release`

Run: `dotnet test tests/Vela.Cli.Tests/Vela.Cli.Tests.csproj --configuration Release --filter FullyQualifiedName~Migration`

Expected: PASS for both providers and all failure states.

- [ ] **Step 5: Commit migrations**

```powershell
git add src/Vela.Data.Runtime/Migrations src/Vela.Cli/MigrationCommands.cs tests/Vela.Data.Runtime.Tests/MigrationRunnerTests.cs tests/Vela.Cli.Tests
git commit -m "feat: add versioned database migrations"
```

### Task 7: Add Vela source packages, examples, docs, and CI gates

**Files:**
- Create/modify all `packages/vela.std.http*` and `packages/vela.std.db*` manifests and `src/lib.vela` files.
- Create: `examples/packages/vela-api-sqlite/**`
- Create: `examples/packages/vela-api-postgres/**`
- Modify: `docs/articles/standard-library.md`, `docs/articles/examples.md`, `docs/articles/toc.yml`
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Write failing source-package compilation tests**

Add backend tests compiling imports for every new package and asserting the exact capability list, generated route delegate, parameter types, transaction disposal, and absence of unused adapter references.

- [ ] **Step 2: Run the tests and verify failure**

Run: `dotnet test tests/Vela.Backend.Tests/Vela.Backend.Tests.csproj --configuration Release --filter "FullyQualifiedName~OfficialHttp|FullyQualifiedName~OfficialDatabase" --no-restore`

Expected: FAIL because packages/bindings are absent.

- [ ] **Step 3: Implement source-linked facades and examples**

`vela.std.http.server` exposes server/route/response builders over `Vela.Http.Runtime`; `vela.std.db` exposes provider-neutral records and interfaces; provider packages expose typed data-source factories. Keep existing `http.get` compatibility. The SQLite example serves `/messages` over HTTPS loopback in tests; the PostgreSQL example reads its connection string from `VELA_POSTGRES_CONNECTION` and never embeds credentials.

- [ ] **Step 4: Run complete gates**

Run: `dotnet build Vela.slnx --configuration Release --no-restore`

Run: `dotnet test Vela.slnx --configuration Release --no-build --no-restore`

Run: `docfx docs/docfx.json --warningsAsErrors`

Run: `git diff --check`

Expected: all commands exit 0; CI includes PostgreSQL service/container and platform HTTP tests.

- [ ] **Step 5: Commit packages, examples, docs, and CI**

```powershell
git add packages examples/packages/vela-api-sqlite examples/packages/vela-api-postgres docs/articles .github/workflows/ci.yml tests/Vela.Backend.Tests
git commit -m "feat: expose http and database packages"
```
