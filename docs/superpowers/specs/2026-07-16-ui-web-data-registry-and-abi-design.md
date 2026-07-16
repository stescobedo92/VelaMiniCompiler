# Vela UI, web, data, registry, and ABI v2 design

## Status

The conversational design is approved. This written specification is awaiting
final user review. Implementation has not started.

## Purpose

This release expands Vela from a native systems and command-line language into
a platform that can also build cross-platform graphical applications, secure
HTTP services, data-backed business applications, reusable remote packages,
and independently compiled libraries with a safe complex-value ABI.

The release is one product increment implemented in dependency order. The
compiler and runtime foundations are shared by four delivery streams:

1. typed function values, callbacks, package capabilities, and ABI v2;
2. `vela.ui` desktop, mobile, and web packages;
3. HTTP/TLS plus SQLite and PostgreSQL packages;
4. a remote package protocol, CLI client, and self-hosted registry server.

All generated code, public APIs, examples, diagnostics, documentation, package
metadata, and release notes remain English-only.

## Current baseline

Vela currently emits C# and publishes .NET 10 Native AOT executables and shared
libraries. Source packages are reproducible local path dependencies. Native
package manifests use ABI v1 and describe primitive exports. The exporter has
wire representations for `Text` and `Decimal`, but the importer accepts only
primitive numeric values, `Bool`, and `Unit`. Arbitrary objects and collections
cannot cross a native library boundary.

The standard library contains a bounded plaintext HTTP/1.1 client implemented
over TCP, but no HTTP server, TLS client surface, database drivers, graphical UI
package, or remote registry. This design replaces those limitations without
breaking existing source packages or ABI v1 consumers.

## Goals

1. Provide one declarative Vela UI API for Windows, Linux, macOS, Android, iOS,
   and WebAssembly-capable web targets.
2. Provide a production-oriented HTTP server with TLS, routing, middleware,
   structured JSON, limits, cancellation, graceful shutdown, and optional
   OpenAPI.
3. Provide safe, explicit, asynchronous SQLite and PostgreSQL access with
   parameters, transactions, migrations, pooling where supported, and
   deterministic disposal.
4. Add remote SemVer dependencies, deterministic package artifacts, a sparse
   HTTP registry protocol, content-addressed caching, secure publication, and a
   self-hosted reference registry.
5. Make registry APIs sufficient for a future crates.io-style website without
   changing the package protocol.
6. Introduce ABI v2 for `Text`, `Decimal`, records, options, results, contiguous
   values, collections, objects, callbacks, asynchronous work, and structured
   errors.
7. Preserve ABI v1 import compatibility and local path dependencies.
8. Preserve trimming and Native AOT as default requirements for supported
   application and library targets.
9. Deliver a checked, runnable `examples/ui-hello-form` application containing
   a form, an input initialized to `Hello World`, and a button that displays the
   input value.
10. Publish the completed increment under the next unused semantic version tag
    only after every release gate passes.

## Non-goals

- Building a custom rendering engine or accessibility tree.
- Requiring users to write XAML.
- Providing an object-relational mapper in this release.
- Deploying a public Vela registry or the future registry website. The release
  provides a complete self-hosted registry and a web-ready API.
- Allowing packages to execute arbitrary build scripts, import arbitrary
  MSBuild targets, or run analyzers during dependency resolution.
- Passing raw CLR object pointers, garbage-collected addresses, exceptions, or
  runtime-specific layouts across the native ABI.
- Pretending that Apple targets can be built or signed without a supported
  macOS/Xcode host.
- Removing or rewriting an already published release tag.

## Architectural principles

### Explicit opt-in dependencies

The base Vela runtime remains small. UI, ASP.NET Core, SQLite, and PostgreSQL
dependencies are added to generated projects only when the application imports
the corresponding package. Importing `vela.ui.components` must not add a
database driver; importing `vela.std.db.sqlite` must not add Uno Platform.

Official adapters may add compiler-owned framework or NuGet references from a
signed capability catalog shipped with the SDK. Third-party packages cannot
inject arbitrary MSBuild logic. The resolver records every capability and its
exact managed dependency version in `vela.lock`.

### Source packages before binary packages

Reusable Vela packages remain source-linked by default. This provides the best
cross-platform compatibility, diagnostics, trimming, and whole-program Native
AOT analysis. A package uses ABI v2 only when it is intentionally distributed
as a precompiled native library or interoperates with non-Vela native code.

### Strongly typed generated code

Vela lowers UI trees, route tables, JSON metadata, database calls, callbacks,
and interop bindings to statically analyzable C#. Runtime reflection, dynamic
proxy generation, runtime XAML parsing, and runtime route discovery are not
used.

### Bounded and deterministic behavior

Network bodies, request headers, query results, registry artifacts, archives,
logs, and caches have explicit limits. Package artifacts, lockfiles, generated
manifests, C headers, and route/UI metadata are deterministic.

## Shared language and backend foundation

### Typed function values

UI events, HTTP handlers, registry hooks, and ABI callbacks require typed
function values. Vela adds the type `Fn<(P1, P2), R>` and allows top-level
functions, methods, and lambdas to be passed where an exact function signature
is expected.

```vela
fn greet(name: Text) -> Text {
    return "Hello " + name;
}

let formatter: Fn<(Text), Text> = greet;
```

Function variance is not implicit. Parameter count, parameter types, return
type, and `async` state must match exactly. Capturing lambdas lower to sealed,
trim-safe generated closure types. Non-capturing lambdas lower to cached static
delegates. The compiler rejects a callback that may outlive a captured value
whose lifetime cannot be proven or retained safely.

### Async function values

`async Fn<(P1), R>` represents a callback whose eventual result is `R`.
Cancellation is explicit through the existing `Cancellation` value rather
than injected invisibly. The generated delegate and task shape are statically
known so Native AOT does not require reflection.

### Package capability catalog

The SDK ships a signed, versioned catalog that maps official packages to the
framework references and managed packages required by their generated C#:

| Capability | Added only when imported |
| --- | --- |
| `aspnet-server` | `Microsoft.AspNetCore.App` framework reference |
| `sqlite` | pinned Microsoft.Data.Sqlite adapter set |
| `postgres` | pinned Npgsql adapter set |
| `uno-ui` | pinned Uno Platform target packages |

Capability entries contain exact versions, target restrictions, trimming
metadata, license metadata, and a catalog hash. Unknown capability identifiers
and attempts to override official capability definitions are compiler errors.

### Backend decomposition

The existing monolithic C# emitter is split along the boundaries required by
this release:

- type and callback lowering;
- standard module binding generation;
- UI tree lowering;
- HTTP route and JSON context generation;
- database binding generation;
- ABI import/export generation;
- project and publish staging.

This is a focused decomposition of code touched by the new features. It does
not change unrelated language behavior.

## UI architecture

### Rendering backend

`vela.ui` is a Vela-native declarative API lowered to strongly typed Uno C#
Markup. Uno owns platform controls, layout, rendering, input, themes,
accessibility integration, and platform heads. XAML is not part of the normal
Vela authoring experience and no custom rendering engine is introduced.

An explicit native-control escape hatch is available through
`vela.ui.platform`. It is capability-gated, target-specific, and never weakens
the portable API silently.

### Official package namespace

The `vela.ui.*` namespace is reserved for SDK-owned packages:

| Package | Responsibility |
| --- | --- |
| `vela.ui` | application, window, lifecycle, dispatcher, theme, and run loop |
| `vela.ui.components` | text, button, input, image, list, table, progress, and common controls |
| `vela.ui.forms` | form composition, fields, validation, submission, and error summaries |
| `vela.ui.layout` | row, column, grid, stack, scroll, split, spacing, and alignment |
| `vela.ui.navigation` | pages, routes, tabs, dialogs, and navigation state |
| `vela.ui.state` | observable values, computed values, bindings, and deterministic subscriptions |
| `vela.ui.platform` | target capability checks and controlled native interop |

Third-party components use their publisher namespace, such as
`acme.ui.charts`. A third-party component implements the public `UiNode` and
lifecycle contracts and composes with official layouts without modifying the
compiler.

### Application and component model

`Application` owns the platform lifecycle. `Window` or the mobile/web page host
owns one root `UiNode`. Components are immutable descriptions except for
explicit state bindings. Builders return the receiver to support readable
composition without requiring list literals.

State updates are serialized through the UI dispatcher. Updating UI-bound
state from an invalid thread produces an actionable Vela error in debug builds
and is marshalled only through an explicit dispatcher operation in production
code.

Subscriptions and event handlers implement deterministic disposal. Closing a
window, removing a component, or disposing an application releases native
controls, subscriptions, callbacks, and captured state.

### Forms and validation

`vela.ui.forms` provides typed field bindings and validation results. Required,
length, range, pattern, and custom validators run without reflection. A form
does not submit while validation errors exist. Validation messages participate
in the platform accessibility tree and are associated with their input.

### Accessibility and theming

Every official interactive component exposes accessible name, description,
role, state, keyboard behavior, focus order, and high-contrast behavior.
Platform accessibility services are used through Uno. Themes use typed tokens
for colors, typography, spacing, and control states. Applications support
light, dark, system, and high-contrast modes without platform-specific source.

### Targets

The target matrix is Windows, Linux, macOS, Android, iOS, and web. Each target
has a real build job where GitHub-hosted runners and required toolchains allow
it. Apple signing and device execution remain separate credentialed steps.
Unsupported cross-host requests fail early with a diagnostic that names the
required host and toolchain.

### Required Hello World form

The final checked example is intentionally small:

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

The exact example above is an acceptance contract and must compile unchanged.
The headless UI test enters a new value, activates `Show`, and verifies that the
bound output text receives that value.

## HTTP and TLS architecture

### Package surface

| Package | Responsibility |
| --- | --- |
| `vela.std.http` | structured HTTP client, TLS, request/response bodies, headers, cancellation |
| `vela.std.http.server` | Kestrel server, routes, middleware, JSON, limits, lifecycle |
| `vela.std.http.openapi` | optional compile-time OpenAPI document generation |

The existing plaintext helper remains source compatible during migration but
is documented as a low-level compatibility API. New code uses structured
requests and responses.

### Server and routing

Routes are registered explicitly and compiled into a static route table. Route
parameters, query parameters, headers, JSON bodies, status codes, and response
types are strongly typed. Duplicate or ambiguous routes are compile errors.

```vela
include vela.std.http.server as http;

async fn health(request: http.Request) -> http.Response {
    return http.json(200, "{\"status\":\"ok\"}");
}

async fn main() -> Int {
    let server = http.server("inventory")
        .get("/health", health)
        .listen("0.0.0.0", 8080);
    return await server.run(Cancellation.none());
}
```

Middleware is an ordered typed callback chain. The initial official middleware
surface covers exception mapping, request IDs, structured access logging,
authentication hooks, authorization policies, CORS, compression, body limits,
timeouts, and rate limiting. CORS is closed by default.

### TLS

HTTPS endpoints accept PFX, PEM certificate/key pairs, or supported operating
system certificate-store references. TLS 1.2 is the minimum. HTTP/1.1 and
HTTP/2 are enabled by default where supported; HTTP/3 is explicit because it
has additional platform dependencies.

Production mode rejects development certificates, missing certificates for an
HTTPS endpoint, plaintext certificate passwords in committed configuration,
and weak protocol selection. Secrets are read through configuration providers
or environment-backed secret references and never rendered in logs.

### Resource limits and shutdown

The server has conservative configurable limits for request body size, header
size/count, concurrent requests, queued requests, minimum body data rate, route
execution time, and JSON depth. Limit rejection returns the appropriate 4xx or
5xx response without buffering unbounded input.

Shutdown stops accepting connections, cancels pending work after the configured
grace interval, waits for active requests, disposes database and callback
resources, and exits with a deterministic status.

### JSON and OpenAPI

Request/response serializers use generated `System.Text.Json` contexts. Dynamic
reflection-based serialization is not enabled. `vela.std.http.openapi` derives
schemas from statically known Vela route and record metadata and emits a
deterministic OpenAPI document at build time. The package is optional so a
service without OpenAPI pays no size cost.

## Data architecture

### Provider-neutral contracts

`vela.std.db` defines provider-neutral contracts for `DataSource`, `Connection`,
`Command`, `Parameter`, `RowReader`, `Transaction`, `Migration`, and database
errors. Provider-specific behavior is available only through an explicit
provider package.

| Package | Backend |
| --- | --- |
| `vela.std.db.sqlite` | Microsoft.Data.Sqlite |
| `vela.std.db.postgres` | Npgsql |

Commands expose asynchronous `execute`, `scalar`, and streaming `query`
operations. Query results use a forward-only `RowReader` by default so large
result sets are not materialized. A bounded `collect(max_rows)` helper is
available when an in-memory collection is intentional.

### Parameters and values

External values are always supplied as parameters. SQLite uses named
parameters; PostgreSQL emits positional placeholders for its efficient native
protocol. Supported common values include null, `Bool`, integral and floating
numbers, `Decimal`, `Text`, byte buffers, UUID, date/time values, and JSON text.

Identifiers cannot be supplied through value parameters. Dynamic identifiers
require a separately validated identifier API that rejects unquoted or invalid
names. The standard API never concatenates untrusted values into SQL.

### Connections and pooling

SQLite connections are short-lived and deterministically disposed. The
provider exposes busy timeout, read-only mode, foreign-key enforcement, and
write-ahead logging as typed options. It does not pretend to support
`System.Transactions`.

PostgreSQL owns one thread-safe `NpgsqlDataSource` per normalized configuration
and uses pooling by default. The Native AOT-oriented slim builder is used, and
optional features require explicit capability opt-in. Connections remain open
only for the duration of the operation or explicit transaction.

### Transactions

Transactions are explicit. `commit` makes changes durable; leaving the scope
without a successful commit rolls back. Nested transactions are supported only
through provider savepoints when explicitly requested and supported. Commands
cannot silently migrate between connections while a transaction is active.

### Migrations

The release includes a migration runner, not an ORM. Migration files have a
strict monotonically increasing identifier, description, provider selection,
content hash, and SQL body. Applied migrations are recorded in a dedicated
table. A changed hash for an already applied migration fails verification.

Migration application acquires a provider-appropriate lock, runs transactionally
where supported, records success atomically, and stops on the first failure.
Commands cover create, status, validate, apply, and generate an empty migration
template. Automatic schema inference and runtime model reflection are excluded.

## Remote package registry

### Selected architecture

Vela uses a dedicated registry with a sparse HTTP metadata index and immutable,
content-addressed package artifacts. This provides better package ownership,
search, yanking, auditing, and future website support than reusing NuGet or a
Git-only index.

The SDK includes both the client protocol and `vela-registry`, a self-hosted
reference server. The reference server is written in Vela after the HTTP and
PostgreSQL packages are operational. It is compiled and exercised as an
end-to-end dogfood application and distributed as a Native AOT container image
and platform executable.

### Registry discovery and configuration

Registries expose a versioned service document containing canonical API,
index, download, authentication, documentation, and website URLs plus feature
flags and maximum package size. `vela registry add` validates HTTPS, protocol
version, canonical URL, and registry identity before saving configuration.

There is no fictitious default public endpoint. A user selects a configured
registry until a separately authorized public Vela service exists. Private
registries may require authentication for metadata and downloads.

### CLI commands

The CLI adds:

- `vela registry add|remove|list|login|logout`;
- `vela search`;
- `vela add|remove|update`;
- `vela package`;
- `vela publish`;
- `vela yank|unyank`;
- `vela owner add|remove|list`.

Commands support `--registry`, machine-readable JSON output, cancellation, and
non-interactive CI credentials. Secrets never appear in arguments printed by
the CLI, logs, lockfiles, or manifests.

### Package identity and SemVer

Package names use lowercase ASCII segments separated by dots. Each segment
starts with a letter and contains letters, digits, or hyphens. Names are
compared using their canonical lowercase form, and collisions involving case,
hyphen/underscore equivalence, reserved operating-system names, or Unicode
confusables are rejected.

Versions follow SemVer 2.0.0. Dependencies may declare exact versions and
compatible ranges. The resolver is deterministic: equal solutions use the
highest non-yanked compatible stable version, package name order, and canonical
registry order. Prereleases are selected only by an explicit prerelease range.

Once published, a package version and its metadata are immutable. Yanking
prevents new resolution but does not break an existing lockfile. Deletion is an
administrative exceptional operation and never a normal publisher command.

### `.vlpkg` artifact

`.vlpkg` is a deterministic ZIP archive with normalized forward-slash paths,
UTF-8 names, fixed timestamps, stable entry order, and no duplicate entries.
It contains the manifest, Vela source, selected documentation, README, license,
and explicitly declared assets. Build outputs, VCS data, secrets, symlinks,
device names, absolute paths, and parent traversal are rejected.

The server revalidates the archive, manifest identity, version, dependency
metadata, capability declarations, uncompressed-size ratio, and file limits.
The server computes the SHA-256 digest; the client never chooses the canonical
digest.

### Signed registry metadata

Registry trust metadata follows The Update Framework (TUF) 1.0 role and client
workflow instead of defining a new signing protocol. The Vela registry profile
fixes the permitted algorithms, canonical JSON encoding, paths, expiration
ceilings, consistent-snapshot behavior, and key thresholds so independent Vela
clients and registries remain interoperable.

`vela registry add` pins a threshold-signed registry root identity after showing
its human-readable name and SHA-256 fingerprint. Registry root rotation
requires signatures from the threshold established by the previously trusted
root. Short-lived signed timestamp and snapshot metadata prevent rollback and
freeze attacks. Signed target metadata binds every immutable package version to
its archive digest, size, manifest hash, and publication time.

Clients verify the complete root, timestamp, snapshot, target, and artifact
chain before an archive enters the content-addressed cache. Expired metadata,
version rollback, inconsistent snapshots, unknown keys, insufficient
signatures, and digest mismatch are hard errors. Loopback development
registries can use an explicitly trusted development root; signature checks are
never disabled implicitly.

### Lockfile and resolver

`vela.lock` records the exact package name, version, canonical registry URL,
artifact SHA-256, manifest hash, dependency edges, selected features,
capabilities, target conditions, and ABI contract hashes. Path dependencies
record their canonical source hash and remain supported.

`--locked` rejects a solution change. `--frozen` also prohibits network and
cache mutation. `--offline` permits an unlocked solution only from verified
cached metadata and artifacts. Hash mismatch, registry identity drift, and
missing locked content are hard errors.

### Cache

Artifacts are stored once in a content-addressed cache. Metadata uses `ETag`
and conditional HTTP requests. Downloads go to a uniquely named temporary
file, are size-limited while streaming, verified, atomically renamed, and then
extracted into a read-only content-addressed directory.

Concurrent Vela processes coordinate cache writes through bounded locks. Cache
pruning never removes content referenced by active workspace lockfiles supplied
to the prune command.

### Authentication and ownership

Interactive tokens are stored through operating-system credential providers.
CI uses an environment variable or credential-provider process without writing
the token to disk. Publishing tokens are scoped by registry, package prefix,
operation, and expiration. The server stores only secure token hashes.

The publish API supports OIDC trusted publishing for CI so a release workflow
does not require a long-lived registry token. Each accepted publication
produces a signed receipt containing publisher identity, package/version,
artifact digest, manifest hash, and timestamp. Official `vela.*` packages must
use trusted publishing from an allowlisted repository and workflow identity.

Packages have individual and organization owners. Official `vela.*` prefixes
are registry-reserved. Ownership changes, token changes, publication, yanking,
and administrative actions produce immutable audit events.

### Future crates.io-style website contract

The first registry API includes all data needed by the future website:

- package name, summary, README, documentation URL, repository, homepage, and
  license expression;
- categories, keywords, supported Vela editions, compiler ranges, platforms,
  capabilities, and package kind;
- versions, release time, yanked state, artifact size, checksum, dependencies,
  ABI metadata, and changelog URL;
- owners, organizations, verified namespaces, and security contact;
- total downloads, per-version downloads, recent downloads, and dependent
  package counts;
- paginated search with stable cursors and sorting by relevance, downloads,
  recency, or name;
- audit-safe moderation and advisory flags.

Counters are asynchronous analytics, not part of dependency resolution, so
website traffic cannot change a build result. The API is versioned and returns
stable identifiers suitable for links and search indexing.

### Reference server storage

PostgreSQL stores identity, ownership, package metadata, version records,
tokens, audit events, and download aggregates. Package blobs use a storage
interface with a safe filesystem implementation for self-hosting and a future
object-storage adapter. Blob paths are derived only from validated hashes.

The server supports TLS directly for self-hosting and can also run behind a
trusted reverse proxy. Proxy headers are ignored unless the proxy network is
explicitly configured.

## ABI v2

### Alternatives and selected model

Directly marshalling every Vela object as a C structure would expose unstable
managed layouts. Serializing every value as JSON would be safe but slow, lose
type fidelity, and create avoidable allocations. ABI v2 therefore uses a hybrid
model:

- fixed wire values for primitives and simple records;
- borrowed or owned contiguous buffers for text and flat immutable data;
- opaque reference-counted handles for objects and mutable or generic values;
- function pointer/context pairs for callbacks;
- task handles for asynchronous operations.

### Manifest and compatibility

ABI v2 manifests contain:

- manifest and ABI schema versions;
- package, package version, target triple/RID, architecture, endianness, and
  calling convention;
- minimum compatible Vela runtime ABI;
- exported logical names and stable native symbols;
- complete parameter/result descriptors;
- record field offsets, size, alignment, discriminants, and element types;
- ownership (`borrowed`, `owned`, `shared`) and nullability;
- callback and async operation descriptors;
- required lifecycle symbols;
- canonical contract hash.

Consumers reject an unknown major ABI, incompatible target, invalid layout,
missing lifecycle symbol, or contract mismatch before loading the library.
ABI v1 manifests and primitive imports continue to work. New libraries emit
ABI v2 by default; a deliberate compatibility option may emit v1 only when
every export is v1-safe.

### Primitive and fixed values

Integer and floating types retain fixed-width C representations. `Bool` is an
8-bit value restricted to zero or one. `Decimal` remains four 32-bit words and
validates reserved bits and scale before conversion. Enums declare a fixed
integer representation.

ABI-safe records use sequential explicit layout and contain only ABI-safe
fields. Nested record layouts are flattened into the contract hash. Adding,
removing, reordering, or changing a field changes the contract.

`Option<T>` and `Result<T, E>` use explicit tagged unions when every contained
type is fixed-layout. Otherwise, they use an opaque handle. Invalid tags are
reported as ABI errors rather than interpreted.

### Text and buffers

Borrowed input text is a UTF-8 pointer and byte length valid only for the call.
Returned owned text includes pointer, length, allocator identity, and a matching
release operation. Embedded NUL bytes are allowed because length is explicit.
Invalid UTF-8 is rejected at the Vela `Text` boundary.

Flat immutable arrays of ABI-safe values use pointer, length, element size, and
element contract identifier. Returned owned buffers use the same explicit
release contract. The importer copies only when ownership, alignment, target
layout, or mutability requires it.

### Objects and collections

Classes, interfaces, generic collections, mutable collections, strings owned by
another library, and arbitrary object graphs cross the ABI as opaque handles.
A handle contains an owner-library identity, type contract identifier, slot,
and generation. It never contains a raw managed object pointer.

Each library owns a thread-safe handle table and exports retain, release, type
query, and operation entry points. Generation checks prevent reuse of stale
slots. Reference-count underflow, wrong-owner use, wrong-type use, and access
after release return structured ABI errors.

Collection handles expose bounded typed operations such as count, get, set,
append, and iterator creation according to the collection contract. Iterators
have their own lifetime and detect invalidating mutation. Bulk operations use
contiguous buffers when possible to avoid per-element calls.

### Callbacks

An ABI callback is a function pointer plus an opaque context handle, exact
signature descriptor, ownership rule, and optional context release function.
The compiler generates static unmanaged thunks. Captured Vela state is stored
in the owning handle table, not exposed as a garbage-collected pointer.

The caller specifies whether a callback is borrowed for one call, retained
until explicit unregistration, or consumed. Callback invocation validates the
signature and catches all Vela exceptions before returning across the native
boundary. This model supports UI events, HTTP handlers, database hooks, and
native library callbacks.

### Asynchronous operations

Native entry points remain synchronous at the C boundary. An exported async
operation returns a task handle. Standard lifecycle operations support poll,
wait with timeout, cancellation, completion callback registration, result
extraction exactly once, error extraction, and release.

Cancellation is cooperative and race-safe. Releasing an incomplete task
requests cancellation and releases resources after completion; it does not
free state still used by native or managed work.

### Error contract

No managed or Vela exception crosses an unmanaged entry point. ABI v2 calls
return a stable status code and place successful values in an out-result. An
optional owned error value contains a stable Vela error code, category,
message, source package, and causal metadata safe for the boundary.

Panics, allocation failure, invalid arguments, invalid UTF-8, stale handles,
type mismatch, cancellation, timeout, and provider errors have distinct status
categories. Import bindings convert them into the documented Vela exception or
`Result` shape at the source-language boundary.

### Generated C header

Library builds emit a deterministic C11-compatible header containing fixed
wire structures, status codes, symbols, calling convention macros, ownership
comments, and lifecycle functions. Platform export lists are generated from
the same manifest model. Header and manifest contract hashes must agree.

## Error handling and diagnostics

Static misuse produces Vela diagnostics before generated C# compilation. New
diagnostics cover:

- callback signature and lifetime mismatch;
- invalid UI composition, state binding, route, validator, or target;
- unsafe TLS and invalid HTTP limits;
- invalid SQL parameter use, transaction state, or migration sequence;
- registry protocol, package identity, SemVer, lockfile, checksum, namespace,
  archive, authentication, and capability errors;
- ABI layout, ownership, handle, callback, async, target, and version errors.

Every diagnostic has a stable code, exact source or manifest span, actionable
help, deterministic ordering, and no generated-C# implementation leakage.
Dynamic failures use typed Vela exceptions or explicit `Result` values. HTTP
exception middleware maps only documented public errors; unexpected internal
errors return a generic response and retain details only in protected logs.

## Security requirements

- All production HTTP endpoints default to TLS and bounded resource limits.
- SQL values use parameters; credentials and certificate secrets never enter
  source-generated logs.
- Registry metadata and artifacts are accepted only over validated HTTPS unless
  the user explicitly configures a loopback development registry.
- Package archives reject traversal, symlinks, absolute paths, decompression
  bombs, duplicate names, device files, and unsupported encodings.
- Remote package versions are immutable and verified against registry-computed
  SHA-256 hashes, threshold-signed registry metadata, and the lockfile.
- Package resolution cannot execute package code or arbitrary build logic.
- Native libraries load only from locked canonical paths and validated ABI
  manifests.
- ABI handles never expose managed addresses and all boundary exceptions are
  converted to statuses.
- UI native escape hatches are capability-gated and target-specific.
- Registry tokens use least privilege, expiration, secure storage, and audit
  events.
- Sensitive values are redacted structurally, not by best-effort string
  replacement.

## Performance requirements

- Non-capturing callbacks are cached and allocation-free after initialization.
- UI updates batch compatible property changes and do not rebuild unchanged
  subtrees.
- HTTP route lookup is generated and allocation-conscious; bodies stream by
  default.
- PostgreSQL uses one data source and pooling; SQLite avoids long-lived write
  locks.
- Database queries stream by default and in-memory collection requires an
  explicit maximum row count.
- Registry metadata uses sparse requests, `ETag`, compression, and a
  content-addressed cache.
- ABI flat values and buffers avoid handles and copies when safe; bulk
  collection operations avoid per-element transitions.
- Every optional framework reports its executable size contribution separately
  from the base-runtime budget.

## Testing strategy

### Compiler and language

- Parser and semantic tests for function types, method groups, sync/async
  lambdas, captures, generic inference, lifetime errors, and diagnostics.
- Generated C# tests for static delegates, closure types, route tables, JSON
  contexts, UI trees, capability references, and Native AOT annotations.
- Regression tests for all existing syntax, source packages, and ABI v1.

### UI

- Unit tests for component composition, state propagation, validation,
  dispatcher rules, lifecycle disposal, accessibility metadata, themes, and
  target diagnostics.
- Headless tests for the Hello World form: initial input value, button event,
  displayed value, validation state, and resource disposal.
- Build matrix for Windows, Linux, macOS, Android, iOS, and web using honest
  host/toolchain constraints.
- Native AOT or the platform's supported ahead-of-time mode is validated for
  every release target where Uno supports it.

### HTTP and data

- Real loopback HTTP/1.1 and HTTP/2 tests, TLS certificate tests, malformed
  request tests, body/header/concurrency limits, cancellation, rate limiting,
  middleware order, graceful shutdown, JSON source generation, and OpenAPI
  snapshots.
- SQLite file and in-memory integration tests for parameters, concurrent reads,
  busy behavior, transactions, rollback, migrations, cancellation, and
  disposal.
- PostgreSQL container integration tests for pooling, positional parameters,
  streaming, transactions, savepoints, migrations, cancellation, TLS, and
  connection failure.
- SQL injection regression tests demonstrate that hostile values remain data.

### Registry

- Resolver tests for SemVer ranges, prereleases, yanked versions, duplicate
  sources, cycles, conflicts, deterministic tie-breaking, local paths, and
  target-specific dependencies.
- Archive fuzz and boundary tests for every rejected path and size condition.
- In-process and container end-to-end tests covering publish, sparse metadata,
  authentication, search, add, lock, restore, offline/frozen behavior, yank,
  owner changes, audit events, and cache concurrency.
- Contract tests freeze the API shapes required by the future website.
- The reference registry is compiled from Vela source and serves a package
  consumed by a second Vela project during CI.

### ABI

- Layout tests on every supported architecture for primitives, decimal, text,
  records, tagged unions, buffers, callbacks, and status/error values.
- Cross-library tests for text and decimal round trips, borrowed/owned buffers,
  retain/release, stale and wrong-type handles, collections, iteration,
  callbacks, captured state, async completion, cancellation, and error mapping.
- Native C consumer tests compile against the generated header and invoke the
  shared library.
- ABI v1 consumer tests remain green and v2 version/target/hash mismatches fail
  before symbol invocation.
- Stress tests race retain/release, cancellation/completion, callback
  unregister/invoke, and concurrent collection reads.

### Quality gates

The final change must pass:

- Release build with warnings treated as errors;
- complete unit and integration test suites;
- formatting and `git diff --check`;
- DocFX build with warnings treated as errors;
- checked examples and package smoke tests;
- Native AOT executable and shared-library publication on supported hosts;
- UI target build matrix;
- PostgreSQL container integration;
- self-hosted registry publish/consume integration;
- generated C header consumer integration;
- security-focused review of network, SQL, archives, credentials, native
  ownership, and release workflow changes.

## Documentation deliverables

Documentation adds:

- UI getting started, component/module reference, state, forms, navigation,
  accessibility, themes, target requirements, and native escape hatches;
- HTTP client/server, TLS, middleware, limits, OpenAPI, deployment, and secure
  configuration guides;
- provider-neutral database, SQLite, PostgreSQL, transactions, parameters, and
  migration guides;
- registry configuration, package authoring, SemVer, publishing, ownership,
  private registries, lockfiles, offline builds, and self-hosting;
- ABI v2 wire types, ownership, handles, callbacks, async work, errors, C
  headers, and ABI v1 compatibility;
- the checked Hello World UI form and progressively larger API/business
  application examples.

Every public API has a signature, complexity or resource behavior where
relevant, errors, platform availability, and a checked example.

## Implementation order

1. Refactor touched backend emission boundaries and add capability metadata.
2. Add typed function values, lambdas, callback lowering, and diagnostics.
3. Implement ABI v2 runtime types, manifests, bindings, headers, and v1
   compatibility.
4. Extend the resolver, lockfile, deterministic archive, remote protocol, and
   content-addressed cache.
5. Implement HTTP/TLS and provider-neutral database contracts.
6. Implement SQLite, PostgreSQL, migrations, and their integration tests.
7. Implement the registry server using the new HTTP and PostgreSQL packages.
8. Implement the Uno-backed UI packages, target heads, accessibility, state,
   forms, and platform interop.
9. Add the Hello World UI form and larger HTTP/data/registry examples.
10. Complete cross-platform, Native AOT, security, performance, documentation,
    packaging, and release validation.

The order is dependency-driven, not a reduction of scope. All ten stages are
required for the versioned release.

## Acceptance criteria

The release is complete only when:

1. a Vela UI application builds from one declarative source surface for every
   documented target;
2. `examples/ui-hello-form` compiles and its form/input/button behavior is
   automatically tested;
3. a Vela HTTPS API serves typed routes with bounded requests and clean
   shutdown;
4. the API performs parameterized SQLite and PostgreSQL operations and applies
   validated migrations;
5. a Vela package is published to the self-hosted remote registry, discovered,
   resolved, locked, downloaded, verified, cached, compiled, yanked, and still
   restorable from an existing lockfile;
6. registry APIs expose the complete metadata required by the future
   crates.io-style website;
7. `Text`, `Decimal`, fixed records, options/results, buffers, collections,
   objects, callbacks, async operations, and errors round-trip through ABI v2;
8. ABI v1 primitive consumers remain supported;
9. no package can inject arbitrary build execution and no unmanaged exception,
   raw managed pointer, secret, unbounded archive, or unparameterized external
   SQL value crosses a protected boundary;
10. documentation and examples describe only behavior proven by tests;
11. the full quality-gate matrix is green;
12. the final release commit is pushed and tagged with the next unused SemVer
    version after checking both local and remote tags, and the release workflow
    successfully publishes its artifacts.

## References

- Uno Platform: [supported development environments and targets](https://platform.uno/docs/articles/getting-started/requirements.html)
- Uno Platform: [C# Markup](https://platform.uno/docs/articles/features/using-markup.html)
- Microsoft Learn: [Kestrel endpoint and HTTPS configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)
- Microsoft Learn: [ASP.NET Core Native AOT support](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)
- Microsoft Learn: [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
- Microsoft Learn: [Microsoft.Data.Sqlite parameters](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/parameters)
- Microsoft Learn: [Microsoft.Data.Sqlite ADO.NET limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/adonet-limitations)
- Npgsql: [basic usage, pooling, parameters, and Native AOT guidance](https://www.npgsql.org/doc/basic-usage.html)
- Cargo: [registry index and sparse protocol](https://doc.rust-lang.org/cargo/reference/registry-index.html)
- Cargo: [registry web API](https://doc.rust-lang.org/cargo/reference/registry-web-api.html)
- Semantic Versioning: [SemVer 2.0.0](https://semver.org/)
- The Update Framework: [TUF specification](https://theupdateframework.github.io/specification/latest/)
- GitHub Docs: [OpenID Connect for trusted CI publishing](https://docs.github.com/en/actions/concepts/security/openid-connect)
- Microsoft Learn: [Native AOT interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)
- Microsoft Learn: [building Native AOT libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)
- Microsoft Learn: [native interoperability best practices](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)
