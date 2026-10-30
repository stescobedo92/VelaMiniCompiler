# Changelog

## Unreleased

## 0.4.0 — 2026-10-29

### Security
- Registry publication now requires a configured API key and HTTPS, validates
  package identities and bounded archives, and publishes versions atomically
  without overwriting existing content.
- Restore requires an explicitly provisioned trusted TUF root by default.
  `--trusted-root` bootstraps registry trust; `--allow-unsigned` is an explicit
  development opt-out and cannot downgrade an already trusted registry.
- Verify consecutive, dual-signed root rotations, persist rollback state, and
  verify cached package bytes before re-extracting them for reuse.
- Isolate caches by registry source and reject unavailable, partial, malformed,
  or expired signed metadata instead of silently accepting unsigned content.
- Validate LSP frames, JSON-RPC messages, Unicode, and message-size limits.

### Fixed
- Uninitialized reference-bearing array slots now raise catchable Vela errors;
  numeric defaults, optional `None`, and `Any` null values remain supported.
- Optional/null, struct, and result equality now generate valid C#, with single
  operand evaluation; unused catch bindings no longer break generated builds.
- JSON integer lookups safely reject other types, closed database operations
  report Vela errors, and handle cleanup preserves lifecycle invariants.
- Invalid task handles cannot cancel another task occupying the same slot.
- GraphQL arguments preserve quoted commas and escapes, and responses remain
  valid JSON; unsupported documents are rejected before invoking resolvers.
- HTTP package ranges select the highest compatible version, matching file
  registry semantics.
- Formatter idempotence tests cover both LF and CRLF checkouts on every platform.

### Migration
- Existing unsigned registries require `vela package restore ... --allow-unsigned`.
  Obtain trusted root metadata through an authenticated, independent channel
  before restoring signed packages with `--trusted-root`.
- Configure `Registry__ApiKey` for publishing to the minimal registry. Plain HTTP
  publishing requires explicit development configuration; existing versions are
  immutable and return HTTP 409 on duplicate publication.
- The signed registry protocol remains a restricted root/targets design, not a
  full TUF implementation with snapshot/timestamp roles and delegated targets.

## 0.3.1 — 2026-07-19

### Added
- ABI import for `Text`/`Decimal`, C11 ABI headers, and ABI v2 ownership runtime
  (`VelaHandleTable`, `VelaAbiBuffer`, task handles).
- Production ABI v2 export protocol: status codes, `out` buffers, and
  `vela_buffer_release` lifecycle (legacy ABI v1 imports still supported).
- ECDSA P-256 signed SDK capability catalog wired into generated project files.
- Broader capturing lambdas / `Fn` values and function-value invocation.
- SQLite and PostgreSQL adapters (`vela.core.sqlite` / `vela.core.postgres`) with
  migrations; TLS HTTP client helpers (`http.request_get` / `http.request_post`).
- Package restore (`vela package restore`) via `Vela.Packages`, optional TUF
  root/targets verification, advanced SemVer ranges, plus `Vela.Registry.Server`
  MVP, registry dogfood example, and `vela-lsp` language-server foundation.
- `vela package` command group for the Vela gallery publish workflow:
  `pack` creates a reproducible `.vpkg` archive (embedded `vpkg.json` with
  SHA-256 file hashes, SemVer 2.0 validation, `--version` override), `login`
  and `logout` manage per-source API keys in `~/.vela/credentials.json`, and
  `push` uploads to a registry, resolving NuGet-style `v3/index.json` service
  indexes to their `PackagePublish` endpoint.
- Five new built-in collections modeled on the most used containers in C++,
  Go, Java, and Rust: `SortedMap<K, V>` (std::map/TreeMap/BTreeMap),
  `SortedSet<T>` (std::set/TreeSet/BTreeSet), `Deque<T>`
  (std::deque/ArrayDeque/VecDeque), `PriorityQueue<T>` binary min-heap
  (std::priority_queue/BinaryHeap), and `LinkedList<T>` (std::list/LinkedList).
- `SortedSet`, `Deque`, and `LinkedList` are iterable with `for`; sorted sets
  iterate in ascending order. `SortedMap` supports indexed reads and writes.
- Ordering validation: sorted and priority collections require keys/elements
  with a defined ordering (numeric, `Bool`, or `Text` with ordinal comparison),
  reported at compile time as VEL3006.
- `examples/collections-extended.vela` demonstrating the new collections.

### Changed
- `CSharpEmitter` split into focused partial modules (`Ffi`, `CoreModules`,
  `Collections`, `Statements`, `Declarations`) without changing hot-path cost.
- `global.json` rollForward set to `latestFeature` for .NET 10 SDK hosts.

## 0.3.0 — 2026-07-18

### Added
- Cross-platform Avalonia GUI (`vela.core.gui`): layouts, menus, file dialogs,
  lists/grids, typed callbacks, slider/textarea/numeric/radio/separator.
- REST hosting (`vela.core.http`) on Kestrel with GET/POST/PUT/DELETE handlers.
- Minimal GraphQL (`vela.core.graphql`) mounted on HTTP.
- Unary gRPC (`vela.core.grpc`) with method-name dispatch.
- Adapter runtimes: `Vela.Http.Runtime`, `Vela.Grpc.Runtime` (plus existing UI).
- Lambdas returning `Text` and `fn(value: Int) -> Void` for API/UI handlers.
- Mintlify documentation site under `docs/` (local preview via `mint dev`).

### Changed
- Release staging includes `http-runtime/` and `grpc-runtime/` beside `ui-runtime/`.
- CI checks API/GUI examples and adapter test projects.
- GitHub Actions release flow reads `VERSION` and ships docs validation with tags.

## 0.2.0

- Desktop GUI foundation and installer PATH improvements.

## 0.1.0

- Initial public compiler release.
