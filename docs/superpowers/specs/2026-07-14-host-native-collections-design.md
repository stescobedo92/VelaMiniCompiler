# Host-native publishing and collections design

## Purpose

This increment makes `vela build` behave like a native compiler's default
build command: it produces the primary executable for the machine on which the
compiler runs. It also introduces a compact, explicit standard-collection
surface with documented complexity and language support for constructing,
indexing, mutating, and iterating those collections.

The design copies the useful guarantees of .NET and Go without claiming that
their internal implementations are identical. .NET `List<T>` grows when it
exhausts capacity, .NET dictionaries are hash tables, and Go slices and maps
have their own runtime representations. Vela exposes the corresponding
complexity contracts; it does not expose either platform's private layout.

## Scope

### Host-native build output

`vela build <file.vela> --output <directory>` uses `--target auto` by default.
The auto target passes the current .NET runtime identifier as an opaque runtime
identifier to the publishing toolchain. The compiler does not split or
reconstruct that identifier. This follows the .NET guidance that runtime
identifiers are opaque and allows the installed SDK to select the appropriate
host assets.

The command writes its primary artifact directly into the requested directory:

```text
Windows: dist/hello/hello.exe
Linux:   dist/hello/hello
macOS:   dist/hello/hello
```

The actual file name is discovered from the publishing output rather than
inferred by parsing a runtime identifier. A successful build prints the stable,
absolute `Executable:` path. `BuildResult` includes that path so callers do not
need to duplicate discovery logic.

An explicit `--target <rid>` remains supported. It is a request to the local
.NET SDK, not a promise that every host can cross-compile Native AOT for every
target. If the installed SDK or native toolchain cannot satisfy it, the CLI
returns a `VEL9xxx` publishing diagnostic with the toolchain output. The
`vela targets` command displays the auto target used by this host and explains
how to supply an explicit RID.

Publishing first writes to a unique temporary staging directory. Only after
`dotnet publish` succeeds and the primary executable is found are artifacts
moved into the requested output directory. A failed build removes staging and
does not create or replace the primary executable.

### Collections and complexity

The runtime supplies these Native AOT-compatible collection types:

| Vela type | Representation | Fast operations | Notes |
| --- | --- | --- | --- |
| `Vector<T>` | contiguous dynamic array | indexing O(1); append and pop O(1) amortized | growth and `reserve` can copy O(n) elements |
| `HashMap<K, V>` | .NET hash table wrapper | lookup, set, remove O(1) expected | hash quality and collisions prevent a universal worst-case O(1) claim |
| `HashSet<T>` | .NET hash set wrapper | add, contains, remove O(1) expected | same hashing qualification as `HashMap` |
| `Queue<T>` | circular queue wrapper | enqueue, dequeue, peek O(1) amortized | a capacity expansion can copy elements |
| `Stack<T>` | contiguous stack wrapper | push, pop, peek O(1) amortized | a capacity expansion can copy elements |
| `RingBuffer<T>` | fixed circular array | enqueue, dequeue, peek O(1) | never reallocates; full insertion returns `false` |
| `BitSet` | packed `ulong` words | set, clear, contains O(1) | a capacity expansion can allocate and copy words |

`Vector<T>` is the canonical dynamic array. Existing `List<T>` source syntax is
kept as a type-equivalent compatibility alias, including list literals, so
existing Vela programs remain valid. Both names lower to the same runtime type.

All hash collections use the runtime's default generic equality comparer. This
matches normal .NET collection semantics and preserves Native AOT compatibility.
The semantic analyzer rejects mutable collection values as hash keys; primitive
types and generated records remain valid keys. `Text` uses ordinal equality in
Vela's generated runtime APIs so collection behavior never depends on current
culture.

### Language surface

The parser and semantic analyzer add three focused features needed to use the
collections naturally:

1. Generic constructors, such as `Vector<Int>(128)` and
   `HashMap<Text, Int>(256)`. The optional integer argument is an initial
   capacity; omitted means an empty collection with the runtime default.
2. Postfix indexing for reads and assignments, such as `values[0]` and
   `scores["Ada"] = 42`.
3. `for item in collection:` iteration over Vela collections.

The collection API is deliberately small and uniform:

```vela
fn main() -> Int:
    var values = Vector<Int>(128)
    values.append(7)
    values.append(11)
    values[0] = 9

    var scores = HashMap<Text, Int>(64)
    scores.set("Ada", 42)
    let score = scores.try_get("Ada")

    for value in values:
        print(value)

    0
```

`Vector` supports `append`, `pop`, `count`, `capacity`, `reserve`, `clear`,
and index reads and writes. `HashMap` supports `set`, `try_get`, `contains`,
`remove`, `count`, `reserve`, and indexed reads and writes. `HashSet` supports
`add`, `contains`, `remove`, `count`, and `reserve`. Stack and queue removal
and peek operations return `Option<T>` rather than throwing for an empty
collection. `RingBuffer.try_enqueue` returns `Bool`; its dequeue and peek
operations return `Option<T>`. `BitSet` supports `set`, `clear`, `contains`,
and `capacity`.

Index access is bounds-checked. A missing `HashMap` key through index access
raises a descriptive managed exception, mirroring C# dictionary index access.
Vela code that needs Go-style presence checking uses `try_get`, which returns
`Option<T>`.

The semantic model validates receiver types, argument counts, argument types,
generic constructor capacity values, index types, assignment mutability, and
the iteration element type before C# is generated. It reports compiler
diagnostics instead of allowing generated C# errors to leak to users.

## Architecture

```text
CLI arguments
  -> BuildTargetResolver (auto or explicit RID)
  -> VelaBuildService (temporary publish, artifact discovery, atomic move)
  -> BuildResult (primary executable path)

Vela source
  -> lexer/parser (for, in, generic constructor, index expression)
  -> semantic analysis (collection built-ins and iteration contracts)
  -> C# emitter (runtime collection calls and foreach)
  -> Vela.Runtime (AOT-safe collection implementations)
```

`BuildTargetResolver` owns host identification. `VelaBuildService` owns file
layout and process execution. The CLI only parses user arguments and displays
the result. This keeps target behavior testable without running the CLI process.

Runtime collection classes are independent, public, and generic where useful.
They neither depend on compiler syntax nodes nor perform reflection or dynamic
code generation. The backend maps a checked collection operation to a known
runtime method; it does not assemble arbitrary member names from source input.

## Error handling and security

- Existing `ProcessStartInfo.ArgumentList` invocation remains mandatory; user
  input is never assembled into a shell command.
- Application names remain sanitized before becoming project or artifact names.
- Output and staging paths are normalized before file operations.
- Artifact discovery accepts only the known application executable in the
  publish root; it does not select arbitrary files by extension.
- Collection capacities must be non-negative integer values. Runtime APIs check
  capacity and index boundaries and throw standard descriptive exceptions only
  after source-level checks cannot prove safety.
- A collection constructor, member call, index operation, or loop that cannot
  be typed produces a stable `VEL3xxx` diagnostic.

## Verification

The change includes:

- Unit tests for host target resolution, artifact discovery, staging cleanup,
  and direct primary-artifact placement.
- Lexer and parser tests for `for`/`in`, generic constructors, and indexing.
- Semantic tests for every collection operation, invalid capacity, unsupported
  hash key, incorrect receiver, and loop element binding.
- Runtime tests covering empty cases, capacity expansion, collision-safe map
  semantics, ring-buffer wraparound, and bit operations.
- Backend tests that assert emitted C# uses the intended runtime API.
- End-to-end tests that compile and run a host Native AOT executable and assert
  the reported path exists with the native file name for the current host.
- A reproducible benchmark project that records collection workloads without
  making wall-clock thresholds part of correctness tests.

The acceptance gate is a Release build, the complete test suite, executable
publication and execution on the active host, `git diff --check`, and an
English-text scan over changed user-facing source and documentation.

## Non-goals

- Reimplementing Go's private Swiss Table algorithm or .NET's private
  `Dictionary<TKey, TValue>` implementation.
- Claiming worst-case O(1) for hash tables.
- Adding concurrent collections, unsafe pointers, manual memory management,
  package imports, pattern matching, or a package manager in this increment.
- Guaranteeing cross-platform Native AOT builds when the local SDK lacks the
  required platform toolchain.

## References

- Microsoft Learn: [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- Microsoft Learn: [.NET runtime identifier catalog](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog)
- Microsoft Learn: [`List<T>.Add` complexity](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.add?view=net-10.0)
- Microsoft Learn: [`Dictionary<TKey, TValue>`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)
- Go Blog: [arrays, slices, and append](https://go.dev/blog/slices)
- Go source: [runtime maps](https://go.dev/src/internal/runtime/maps/map.go)
