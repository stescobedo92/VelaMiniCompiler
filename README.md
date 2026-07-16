# Vela

Vela is an experimental, statically checked language and compiler written in C#.
It produces a native executable for the current operating system by default and
uses a compact, source-aware runtime. Its syntax deliberately follows the
familiar C#/Java model: braces delimit blocks and semicolons terminate simple
statements.

## Why Vela

- **Native by default.** `vela build` publishes a Native AOT `.exe` on Windows,
  an executable on Linux/macOS, or a native shared library (`.dll`, `.so`, or
  `.dylib`) for a library package.
- **Small deployable output.** The verified Windows `vela-app` sample, including
  `main.exe`, `vela_math.dll`, and the ABI manifest, is about **1.77 MiB**. Debug
  symbols and XML documentation are not deployed with a native artifact.
- **Predictable safety.** Integer and decimal arithmetic is checked; divide by
  zero, floating non-finite results, null dereferences, invalid casts, and array
  bounds failures become Vela runtime errors with source locations.
- **Fast core collections.** `Vector`, `HashMap`, `HashSet`, `Queue`, `Stack`,
  `RingBuffer`, and `BitSet` use Native-AOT-safe .NET data structures. Hash-map
  and hash-set lookup/insert/remove are expected O(1); vector indexing is O(1).
- **A familiar object model.** Classes use mandatory primary constructors and
  may implement any number of compiler-validated interfaces. Structs, records,
  generics, arrays, nullable values, boxing, and checked unboxing are available.
- **Explicit domain flow.** `Option<T>` and `Result<T,E>` have typed factories,
  while exhaustive `match` catches missing enum or result cases at compile time.
- **Local, reproducible packages.** A `vela.toml` graph resolves deterministic
  local dependencies and records their manifest hashes in `vela.lock`.
  `source-library` packages are linked as Vela source (so they retain classes,
  enums, generics, and `Option<T>`), while `library` packages export a native ABI
  manifest for scalar cross-process calls.
- **Clear builds and diagnostics.** The CLI uses color-aware terminal output,
  shows every compiler phase by default, supports `--quiet`, and exposes raw
  Native AOT publishing output with `-vv`.

## Current feature set

- Types: `Void` returns, `Int`, `UInt`, `Long`, `Float`, `Double`, `Decimal`, `Bool`, `Text`
  (`String` alias), `Any`, `Array<T>`, `Option<T>`, `Result<T, E>`, and generic
  collection and tuple types, plus `Future<T>` and `Cancellation` for asynchronous work.
  There is intentionally no `UFloat` type.
- Checked conversions such as `Int(value)`, `UInt(value)`, `Long(value)`,
  `Float(value)`, `Double(value)`, and `Decimal(value)`.
- Named/default arguments with exactly-once evaluation, tuple/record
  destructuring, and compile-time `@since`, `@deprecated`, `@experimental`, and
  `@doc(hidden)` metadata.
- Brace-based `if`/`else`, `for`, `while`, `break`, `continue`, exhaustive
  `match`, and literal
  `switch` cases with no fall-through. Strongly typed `enum` values require an
  exhaustive `switch` unless a `default` branch is present.
- Comments for editor tooling: `//`, nested `/* ... */`, `///`, and `/** ... */`.
  Documentation is preserved with exact spans and validates `@param` and
  `@returns` where applicable.
- Deterministic `defer` with lexical scope, last-in-first-out execution, and
  argument snapshots. `try`/`catch`/`finally` handles source-aware Vela runtime
  exceptions and exposes `error.message` plus `error.source_location`.
- `async fn`, `await`, `Future<T>`, and explicit `Cancellation`. Async TCP keeps
  sync programs unchanged and emits .NET async state machines only when used.
- `include vela.core;` for the core surface and `include package.name as alias;`
  for manifest-declared native or source-linked package dependencies.
- Explicit, Native-AOT-trimmed core modules: `json`, `crypto`, `tcp`, `text`,
  `math`, `time`, `random`, expanded `io`, `encoding`, `env`, `system`,
  `console`, and `vela.concurrent`.
- Source-linked `vela.std.cli` and `vela.std.log` packages for typed command
  definitions, O(1) expected option lookup, level filtering, terminal-aware
  color, stderr routing, and structured JSON that reuses `vela.core.json`.
- `public ffi fn` exports scalar native ABI functions. Cross-package calls
  currently support `Bool`, `Int`, `UInt`, `Long`, `Float`, `Double`, and `Void`;
  `Text` and `Decimal` are emitted as library ABI values but are not yet accepted
  by the importing call generator.

## Repository layout

```text
src/
  Vela.Core/       Source text, diagnostics, lexer, parser, AST, and semantics
  Vela.Runtime/    Checked numeric operations, safe values, collections, ABI types
  Vela.Backend/    C# emission, packages, ABI manifests, and Native AOT publishing
  Vela.Cli/        Color-aware command-line compiler
tests/
  Vela.Core.Tests/ Parser and diagnostic tests
  Vela.Backend.Tests/ Emission, package, and publishing tests
  Vela.Runtime.Tests/ Runtime-value and collection tests
examples/          Runnable Vela source and package examples
docs/              Language and engineering documentation
```

## Prerequisites

- .NET SDK 10.0 or later.
- A host supported by the installed .NET Native AOT toolchain for native builds.

## Build and test

```powershell
dotnet build Vela.slnx
dotnet test Vela.slnx
```

## Install a release

GitHub releases publish Native AOT compiler artifacts for Windows x64, macOS
(Apple Silicon and Intel), and Linux x64. Artifacts receive platform signatures
when release credentials are configured; unsigned releases carry an explicit
warning and still include checksums and GitHub provenance attestations. Windows
offers both an MSI and a normal graphical setup wizard; macOS offers PKG and
DMG; Linux offers DEB and RPM packages. Each installer makes `vela` available
globally after installation. Portable ZIP/TAR archives include the adjacent
runtime support project required by `vela run` and `vela build`; use those
instead of copying the bare executable alone.

For release signing, verification, CI behavior, and exact install commands, see
[the release and installation guide](docs/releasing.md).

## Use the compiler

Run a source file during development:

```powershell
dotnet run --project .\src\Vela.Cli -- run .\examples\factorial.vela
```

Check a source file without producing output:

```powershell
dotnet run --project .\src\Vela.Cli -- check .\examples\types-objects.vela
```

`examples/diagnostics.vela` is deliberately invalid and produces a source-mapped
diagnostic.

Publish a host-native application. `--target auto` is the default:

```powershell
dotnet run --project .\src\Vela.Cli -- build .\examples\factorial.vela `
  --output .\artifacts\factorial
```

On Windows the artifact is `artifacts\factorial\factorial.exe`; on Linux and
macOS it is an executable without an extension. Use `vela targets` to display
the selected target, `--color never` for plain logs, `--quiet` to suppress normal
build progress, or `-vv` to show the raw .NET publishing command output.
In an interactive terminal, Vela uses TrueColor by default; use `NO_COLOR=1`
or `--color never` to disable it.

Build a native library and an application that imports it:

```powershell
dotnet run --project .\src\Vela.Cli -- build .\examples\packages\vela-math --lib `
  --output .\artifacts\vela-math

dotnet run --project .\src\Vela.Cli -- build .\examples\packages\vela-app `
  --output .\artifacts\vela-app

.\artifacts\vela-app\main.exe
```

The final command prints `42` on Windows. The equivalent executable name is
selected automatically on Linux and macOS.

Run a source-linked standard-library package application. No DLL/SO is produced
for these dependencies: Vela links only the required source and Native AOT trims
the result.

```powershell
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-std-app
```

The example uses `vela.std.cli`, `vela.std.config`, `vela.std.log`, and
`vela.std.test`. `vela.std.config` and `vela.std.log` route all JSON work through
the one `vela.core.json` implementation; they do not contain another parser.

Pass arguments to a Vela program after `--`:

```powershell
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-cli-app -- --name Ada --json
```

Check the async plaintext HTTP example without contacting a remote endpoint:

```powershell
dotnet run --project .\src\Vela.Cli -- check .\examples\packages\vela-http-app
```

`vela.std.http` is an opt-in, bounded HTTP/1.1-over-TCP helper. Its first
version intentionally supports trusted plaintext endpoints only; TLS, redirects,
headers, and streaming remain later work so minimal applications keep their
small runtime footprint.

Try the progressive examples:

```powershell
dotnet run --project .\src\Vela.Cli -- run .\examples\control-flow.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\core-text-math.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\secure-message.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\file-json-report.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\language-foundations.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\match-option-result.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\system-exec.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\io-workspace.vela
dotnet run --project .\src\Vela.Cli -- check .\examples\tcp-echo-client.vela
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-std-app
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-cli-app
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-logging-app
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-server-tool
```

`tcp-echo-client.vela` is intentionally only checked by default; execute it
only against a trusted local echo server.

## Vela at a glance

```vela
include vela.core;

class Counter(start: Int) implements Printable {
    var value: Int = start;

    fn increment() -> Int {
        self.value = self.value + 1;
        return self.value;
    }

    fn text() -> Text {
        return "Counter";
    }
}

interface Printable {
    fn text() -> Text;
}

fn main() -> Void {
    let counter = Counter(41);
    let next: Int = counter.increment();
    print(next);
}
```

Read [the language guide](docs/Vela-language.md) for grammar and semantic rules,
and [the collection guide](docs/Vela-collections.md) for collection APIs and
complexity guarantees.

Build and preview the searchable DocFX site locally:

```powershell
dotnet tool restore
dotnet docfx docs/docfx.json --serve
```

The site covers installation, language rules, modules, the standard library,
diagnostics, architecture, performance, examples, and generated public
compiler/runtime APIs.

## Status

Vela is an educational compiler project with a deliberately small, testable
surface. Remote registries, cross-library object/collection ABI handles, and
imported `Text`/`Decimal` call marshalling are planned rather than represented as
completed features.
