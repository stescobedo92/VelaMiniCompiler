# Native packages, safe runtime, and verbose CLI design

## Purpose

This increment turns Vela from a single-source-file compiler into a small,
cross-platform native package toolchain. It introduces Vela source packages,
native shared-library artifacts, a safe and explicit binary ABI, classes,
structs, interfaces, checked core types, nullable values, boxing, and a
detailed build experience inspired by Cargo.

> Grammar update: Vela uses C#/Java-style `{ ... }` blocks and requires `;` for
> simple statements. All earlier indentation-and-colon examples are superseded
> by the current language guide and checked examples.

The design makes two promises that must not conflict:

1. Vela source APIs are pleasant and strongly typed inside a package.
2. A library compiled independently remains safe to load from another native
   Vela binary, even when it was built on another machine for the same target.

The second promise requires an explicit ABI. It is not safe to expose an
arbitrary managed object, generic collection layout, virtual table, or .NET
runtime object directly from a Native AOT library. Vela therefore exposes a
small FFI-safe surface across shared-library boundaries and keeps the complete
class and interface model inside each compiled package.

## Project model

Every package has a manifest and follows a Cargo-like directory layout:

```text
inventory/
  vela.toml
  vela.lock
  src/main.vela       # executable package entry point
  src/lib.vela        # library package entry point, when applicable
  target/<rid>/<profile>/
```

`vela.toml` declares the package identity, source kind, edition, and local
dependencies. The first delivery supports deterministic path dependencies;
there is deliberately no network registry, implicit download, or executable
build script.

```toml
[package]
name = "inventory"
version = "0.1.0"
kind = "application" # or "library"
edition = "2026"

[dependencies]
vela.core = { path = "../stdlib/vela.core" }
acme.math = { path = "../math" }
```

The resolver canonicalizes paths, rejects packages outside an explicitly
requested workspace, detects version conflicts and dependency cycles, and
writes a deterministic `vela.lock`. A source file imports its manifest-declared
dependencies explicitly:

```vela
include vela.core;
include acme.math as math;
```

`include vela.core;` is the explicit source marker for the compiler-supported
core surface: collections, text, conversion, error, and boxing APIs. The
current runtime intrinsics are linked into generated code; an independently
published source-level `vela.core` package remains future work.

## Native artifacts and ABI

`vela build --lib` uses Native AOT shared-library publishing. It emits the
native extension selected by the current target:

```text
Windows: target/win-x64/release/inventory.dll
Linux:   target/linux-x64/release/libinventory.so
macOS:   target/osx-arm64/release/libinventory.dylib
```

The directory also contains `inventory.velaabi.json`. It is canonical JSON
with the package identity, edition, target RID, ABI version, core ABI hash,
logical exports, native symbols, argument ownership, and a SHA-256 contract
hash. Consumers validate this manifest before code generation; generated
loaders validate the target and ABI hash before resolving a symbol.

The compiler generates C# Native AOT entry points with stable names and
`UnmanagedCallersOnly`. It uses the `NativeLib=Shared` publishing model. A
generated application resolves only dependency libraries named by its locked
manifest from its target directory; it never resolves a library path supplied
by source code at run time.

An exported API must be written with `public ffi fn`:

```vela
include vela.core;

public ffi fn add(left: Int, right: Int) -> Int {
    return left + right;
}
```

The current ABI exporter accepts `Bool`, `Int`, `UInt`, `Long`, `Float`,
`Double`, `Decimal`, `Text`, and `Unit`. The current native import generator
accepts the scalar subset `Bool`, `Int`, `UInt`, `Long`, `Float`, `Double`, and
`Unit`. `Text` and `Decimal` use versioned wire values in the exported ABI but
import-side marshalling is deferred. `Any`, `Array<T>`, collections, and user
objects are not ABI parameters or results in this increment.

No CLR object layout is part of the contract. `Decimal` crosses the exported
ABI as Vela's four-word decimal wire value and `Text` as a versioned owned UTF-8
wire value. The ABI manifest records the package, target, stable symbols, and a
SHA-256 contract hash.

`public ffi fn` exports stable C calling-convention symbols. The current ABI
does not yet include a status/error channel, so exported functions should avoid
operations that can throw across the boundary. A status-based exception contract
and opaque core handles are deferred until the shared core runtime is present.

Classes, structs, interfaces, generic user types, and records can be public in
source packages, but are not legal FFI parameter or result types in this
increment. A library can use them internally and expose operations over the
safe ABI. The compiler reports a source diagnostic rather than emitting an
unsafe binary contract.

## Vela source surface

### Visibility, types, and object model

Declarations are package-internal unless prefixed with `public`. Vela adds
`class`, `struct`, and `interface`; fields are initialized by the synthesized
constructor in declaration order, and methods receive an implicit `self`.

```vela
include vela.core;

public interface Printable {
    fn render() -> Text;
}

public struct Point {
    x: Int;
    y: Int;
}

public class Counter implements Printable {
    var value: Int;

    fn increment() -> Int {
        self.value = self.value + 1;
        return self.value;
    }

    fn render() -> Text {
        return "Counter";
    }
}

fn main() -> Int {
    let point = Point(3, 4);
    let counter = Counter(point.x);
    print(counter.render());
    return 0;
}
```

`struct` has value semantics and cannot be `null` unless it is declared
nullable. `class` has reference semantics. Interfaces specify method contracts
and may be implemented by a class or struct. This release does not add class
inheritance, reflection, or dynamic method invocation; composition and
interfaces cover the intended use cases without creating an unstable native
object ABI.

### Core types and arithmetic

The canonical names and representations are:

| Vela type | Representation | Arithmetic policy |
| --- | --- | --- |
| `Int` | signed 32-bit integer | checked |
| `UInt` | unsigned 32-bit integer | checked |
| `Long` | signed 64-bit integer | checked |
| `Float` | IEEE-754 binary32 | finite-result checked |
| `Double` | IEEE-754 binary64 | finite-result checked |
| `Decimal` | `System.Decimal` | checked |
| `Text` | immutable UTF-8 core value | not numeric |
| `Bool` | Boolean | not numeric |
| `Unit` | no value | not numeric |

`String` is a source-compatible alias of `Text`; `UFloat` does not exist.
Integer literals are range-checked against their contextual target type.
Narrowing conversions must be explicit. Integral and decimal operations use
checked arithmetic. Float and double operations check the result for infinity
or `NaN` and produce Vela arithmetic errors rather than silently continuing
with a non-finite value. Division by zero is consistently reported for every
numeric type.

The runtime supplies location-aware `VelaOverflowException` and
`VelaArithmeticException` values. The emitter records source location in the
generated guard calls so a failure identifies the Vela expression rather than
only generated C#.

### Nullability, arrays, and boxing

Every type may be declared nullable with `?`, including value types. `T?`
lowers to Vela's explicit optional representation. `null` is valid only for a
nullable target. The type checker requires a null check or a proven non-null
path before dereferencing a nullable value. Runtime guards remain mandatory at
every dereference that cannot be statically proven, and throw a
`VelaNullReferenceException` carrying the original source location.

`Array<T>` is a fixed-length, contiguous core collection. It provides O(1)
indexed reads and writes. Every access is emitted through a bounds guard and
throws `VelaIndexOutOfRangeException` with the index, length, and source
location when invalid. `Vector<T>` continues to be the resizable array with
amortized O(1) append; its indexed operations use the same error model.

`Any` is the type-erased core value used for boxing:

```vela
include vela.core;

let boxed: Any = 42;
let value: Int = unbox<Int>(boxed);          # checked; invalid type throws
let optional: Int? = try_unbox<Int>(boxed);  # mismatch becomes null
```

Assignment of a primitive, `Text`, struct, or reference to `Any` boxes it.
`unbox<T>` checks the exact Vela runtime type and throws
`VelaInvalidCastException` on a mismatch. `try_unbox<T>` returns `T?` and
does not throw for a type mismatch. `Any` currently remains package-local and
cannot cross a native package boundary.

## Core as Vela source

The current standard surface is imported with `include vela.core;`. Low-level
allocation, text encoding, checked numeric operations, and native ABI values
are compiler-recognized intrinsics implemented by the compact Native AOT
support runtime. A fully Vela-authored distributable core package is a planned
next step; the present compiler exposes the same surface through the explicit
include marker.
while the compiler retains a safe implementation for platform primitives.

Core exposes `Text`, `Array<T>`, `Any`, `Option<T>`, `Result<T, E>`, existing
collections, checked conversion functions, and the standardized Vela exception
types. Public collection contracts retain their existing O(1) or amortized
O(1) guarantees; hash-table operations remain O(1) expected, never falsely
claimed as universal worst-case O(1).

## Build experience and color

The compiler command model becomes:

```text
vela check [path]
vela build [path] [--release] [--lib] [--target <rid>]
vela run [path]
vela test [path]
```

Without an explicit path, commands discover `vela.toml` from the working
directory. `build` is detailed by default, as requested; `--quiet` or `-q`
reduces output to diagnostics, user-program output, and a final result.
`-v` adds cache decisions and resolved symbols. `-vv` also streams every
underlying .NET publish line with a `dotnet` origin marker. All levels preserve
the order of compiler phases.

```text
   Resolving inventory v0.1.0 (C:\work\inventory)
   Including vela.core v0.1.0
   Checking 6 modules and 38 declarations
   Lowering inventory (native ABI v1)
   Generating bindings for acme.math
   Publishing win-x64 release native executable
    Finished release [native-aot] in 2.31s
```

Spectre.Console supplies the terminal renderer. Status verbs, package names,
durations, warnings, errors, code frames, and suggested fixes are rendered
with semantic color roles modeled after Cargo. Vela source snippets are
tokenized before rendering and all user-controlled text is escaped as markup.
The CLI itself is never included in generated Vela executables.

`--color auto|always|never` controls color. `auto` is the default and disables
ANSI sequences for redirected output; `always` permits CI snapshots; `never`
produces a stable plain-text stream. User-program standard output is passed
through unchanged and is never colorized by the compiler.

## Runtime size budget

The compact runtime is a product requirement, not an informal aspiration.
For each supported OS and architecture, the Release Native AOT minimal
application plus the mandatory deployed `vela.core` artifact must remain below
3 MiB (3,145,728 bytes) uncompressed. The existing Windows Native AOT sample
artifacts are approximately 0.93 MiB, so the current baseline leaves room for
the split core implementation.

The budget excludes user-authored assets and optional dependency libraries;
otherwise a user could make an unavoidable size violation merely by embedding
a 10 MiB resource. `vela build` prints a size report split into executable,
core runtime, and user dependency totals. A package that makes the base runtime
exceed the budget fails the build with a dedicated diagnostic. Applications
whose optional content exceeds 3 MiB succeed but explicitly report that the
additional bytes come from user dependencies or assets.

The runtime uses Native AOT Release publishing, trimming, invariant
globalization, no reflection, no runtime code generation, no dynamic proxy,
and a narrow intrinsic layer. Spectre.Console, TOML parsing, and compiler-only
dependencies remain in the compiler process and cannot inflate a Vela program.

## Architecture

```text
vela.toml + vela.lock + .vela files
  -> workspace/package resolver
  -> include graph and source-package loader
  -> lexer/parser
  -> declaration, type, nullability, and ABI checker
  -> Vela intermediate representation
  -> C# Native AOT emitter + core binding generator
  -> package shared library or application publisher
  -> artifact/manifest verifier + size reporter

Spectre.Console CLI renderer observes every phase and formats output.
Vela.Runtime owns checked guards, opaque handles, and ABI lifetime rules.
```

The CLI is separated into parser, build-event stream, renderer, package
resolver, compiler coordinator, and publisher. This permits exhaustive tests
without asserting raw terminal escape sequences or invoking a native build for
every unit test.

## Verification strategy

Tests are required at every layer:

- Lexer, parser, and diagnostic tests for `include`, manifest errors,
  visibility, class/struct/interface declarations, nullable types, `null`,
  arrays, boxing, and FFI declarations.
- Type and flow tests for every supported numeric type, literal boundaries,
  conversion failures, integer/decimal overflow, float/double non-finite
  results, zero division, nullable dereferences, array bounds, and invalid
  unboxing.
- Runtime tests for handle reference counts, `Text` UTF-8 ownership, `Any`
  round trips, `try_unbox`, collection handles, all exception payloads, and
  randomized array/vector bounds scenarios.
- Package tests for path normalization, lockfile determinism, package identity,
  version conflicts, include cycles, duplicate exports, ABI hash changes, and
  target mismatch rejection.
- Backend tests that inspect generated shared-library exports and generated
  consumer bindings, including rejection of an unsafe class, interface, or
  generic type in an FFI signature.
- CLI tests using Spectre.Console's test console for default detailed output,
  `-q`, `-v`, `-vv`, color modes, redirected output, escaped source text, and
  ordering of all phase events.
- Host integration tests that build a Vela library, build an application that
  includes it, load and execute the exported API, then validate its manifest
  and artifact names.
- A Windows/Linux/macOS CI matrix that runs the native integration suite and
  enforces the 3 MiB base-runtime budget on each native host. Cross-target
  requests are validated separately and never pretend to work when the local
  Native AOT toolchain cannot supply the requested target.

The delivery gate is a Release build with warnings treated as errors, the full
test suite, host native executable and shared-library execution, size-budget
verification, `git diff --check`, and a review of all generated public text.

## Security and compatibility rules

- Process arguments continue to use `ProcessStartInfo.ArgumentList`; no source
  or manifest value is assembled into a shell command.
- Manifests and lockfiles reject path traversal, external workspace escapes,
  duplicate normalized keys, and unsupported ABI versions.
- Native library loading uses locked, canonical target paths and manifest
  hashes, not environment-controlled search paths.
- Runtime errors never cross an FFI boundary as unmanaged exceptions.
- Source excerpts, package names, exception text, and diagnostics are escaped
  before Spectre markup is rendered.
- Existing single-file `vela check`, `run`, and `build` use remains supported
  during migration. It uses a synthetic local package and requires an explicit
  `include vela.core` for new core APIs.

## Non-goals

- A public remote package registry or automatic network dependency download.
- Arbitrary class, interface, or generic object graphs across independently
  compiled native library boundaries.
- Reflection, dynamic code generation, unsafe pointers, or user-defined
  native symbols.
- An unconditional three-megabyte cap on user code, resources, or optional
  third-party packages. The enforced cap protects the Vela runtime baseline.

## References

- Rust Cargo: [cargo build](https://doc.rust-lang.org/stable/cargo/commands/cargo-build.html)
- Spectre.Console: [console capabilities](https://spectreconsole.net/console/)
- Microsoft Learn: [Native AOT interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)
- Microsoft Learn: [C# numeric types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-types)
- Go: [organizing a Go module](https://go.dev/doc/modules/layout)
