# Vela language foundations, standard library, and IDE design

## Status

Implemented baseline. The approved design is now represented by documentation
trivia, enum exhaustiveness, `defer`, typed exception handling, source-linked
packages, async/await, explicit cancellation, async TCP, and the initial
`vela.std` packages. A future LSP/editor extension remains deliberately out of
scope; the compiler now preserves the metadata it will consume.

## Purpose

Extend Vela in three compatible layers without duplicating JSON behavior or
inflating the Native AOT baseline:

1. language foundations: documentation comments, enums, deterministic cleanup,
   exception handling, and asynchronous operations;
2. an opt-in `vela.std` library written in Vela where practical; and
3. compiler data that a future Language Server Protocol (LSP) implementation
   can use for VS Code and JetBrains integrations.

The work is intentionally phased. Async I/O and rich inter-package object
exchange depend on the preceding language and package-linking foundations.

## Non-goals

- Do not add another JSON parser or serializer.
- Do not put HTTP, databases, TLS, or web-server dependencies in `vela.core`.
- Do not implement a remote package registry, editor extension, or LSP server in
  the first phase.
- Do not weaken checked arithmetic, source-aware exceptions, or the 3 MiB
  Native AOT budget for small applications.

## Layering and package model

| Layer | Ownership | Inclusion rule | Examples |
| --- | --- | --- | --- |
| `vela.core.*` | compiler/runtime | explicit import; Native AOT-trimmed | `json`, `crypto`, `tcp`, `io` |
| `vela.std.*` | official standard library | explicit dependency/import | `config`, `cli`, `log`, `test` |
| third-party packages | application ecosystem | manifest dependency | `acme.payments`, `community.mail` |

`vela.core.json` is the sole JSON implementation. Every JSON-capable standard
module imports it rather than parsing JSON itself:

```vela
include vela.core.json;

// In vela.std.config or vela.std.http source.
let canonical = json.compact(payload);
```

When `vela.std.config`, `vela.std.http`, or `vela.std.web` needs object or array
traversal beyond the existing scalar accessors, the core module will gain an
opaque `JsonDocument` / `JsonValue` API. It will wrap the existing Vela JSON
runtime implementation and preserve one validation, encoding, and error model.

### Standard library linking

The present native ABI carries scalar values only. Therefore a Vela standard
library that exposes classes, options, collections, requests, or connections
cannot initially be a separate native shared library without losing its public
types. The package system will add a source-linked library kind:

```toml
[package]
name = "vela.std.config"
version = "0.1.0"
kind = "source-library"
```

The resolver compiles the dependency's Vela source as part of the application
compilation. It preserves Vela classes, interfaces, enums, generics, and
`include vela.core.*` imports while allowing Native AOT to trim unused code.
Existing `kind = "library"` packages keep their `.dll` / `.so` ABI behavior for
scalar native exports. Opaque ABI handles are a later, separate enhancement for
rich binary package interoperability.

## Documentation comments and IDE readiness

### Syntax

```vela
// A regular line comment.

/* A regular block comment.
   Block comments may nest. */

/// Finds an active user.
/// @param id Stable user identifier.
/// @returns The user when it exists.
/// @throws VelaIoException When the backing store cannot be read.
fn find_user(id: Int) -> User? {
    return null;
}

/**
 * Represents an immutable account identifier.
 * @example let id = AccountId("a-42");
 */
struct AccountId {
    value: Text;
}
```

- `//` runs through the end of the physical line.
- `/* ... */` is nestable and may span lines.
- `///` and `/** ... */` are documentation comments when they directly precede
  a declaration, permitting only whitespace and other documentation comment
  lines between them.
- Documentation attaches to functions, classes, structs, interfaces, records,
  enums, fields, methods, and public FFI exports. Comments elsewhere remain
  ordinary trivia.
- Supported tags are `@param`, `@returns`, `@throws`, `@example`, and
  `@deprecated`. Unknown tags are preserved for forward compatibility.

### Compiler representation

The lexer emits comments as trivia with their full source spans. Parser token
navigation ignores regular trivia, so comments never affect grammar or emitted
C#. Leading documentation trivia is collected into `DocumentationCommentSyntax`
and attached to the following declaration. The binder validates known tags:

- `@param` must name an existing function or method parameter;
- `@returns` is warned on a `Unit` declaration;
- `@throws` is retained as documentation and does not require a checked
  exception declaration;
- orphan documentation comments receive a warning with a source span.

This preserves exact text and ranges for future hover, completion, navigation,
formatting, and documentation generation. A later `Vela.LanguageServer` will
consume public syntax/binding metadata rather than reimplementing parsing.

## Enums and exhaustive switch

### Syntax and semantics

```vela
enum ConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Failed
}

fn describe(state: ConnectionState) -> Text {
    switch state {
        case ConnectionState.Disconnected { return "offline"; }
        case ConnectionState.Connecting { return "connecting"; }
        case ConnectionState.Connected { return "online"; }
        case ConnectionState.Failed { return "failed"; }
    }
}
```

Enums are strongly typed, ordinal value types; they do not implicitly convert to
or from `Int`. Cases use the existing qualified member syntax. A switch over an
enum is exhaustive when every declared case appears exactly once or when it has
`default`. An enum switch with no `default` and missing members is a compiler
error. Duplicate, unknown, and incompatible cases are compiler errors.

`switch` keeps its existing isolated-block behavior: there is no fall-through
and no `break` is required. Exhaustive switches contribute to definite-return
analysis.

## Deterministic cleanup with defer

### Syntax and semantics

```vela
fn send_message(payload: Text) -> Int {
    let connection = tcp.connect("127.0.0.1", 7007, 1000);
    defer tcp.close(connection);
    tcp.send_text(connection, payload);
    return 0;
}
```

`defer` accepts a call expression followed by `;`. Its call target and arguments
are evaluated when the `defer` statement executes. Deferred calls execute in
last-in-first-out order when the enclosing lexical block exits, including by
`return`, `break`, `continue`, or an exception. A deferred call that throws
propagates normally when no other failure is active. When cleanup also fails
while an exception is propagating, Vela raises `VelaCleanupException`, preserving
both the primary failure and the cleanup failure as explicit properties.

The emitter lowers each block with defers to `try` / `finally` and uses temporary
locals for evaluated arguments. This avoids closure allocations for simple
cleanup and preserves source locations for failures.

## Structured exception handling

### Syntax and semantics

```vela
fn load_config(path: Text) -> Text {
    try {
        return io.read_text(path);
    }
    catch VelaIoException error {
        print(error.message);
        return "{}";
    }
    finally {
        print("config read completed");
    }
}
```

`try` has one or more typed `catch Type identifier` blocks and an optional
`finally` block. `finally` may appear with or without catches. Initial supported
catch types are `VelaRuntimeException`, `VelaIoException`,
`VelaNetworkException`, `VelaFormatException`, `VelaOverflowException`,
`VelaArithmeticException`, `VelaNullReferenceException`,
`VelaIndexOutOfRangeException`, and `VelaInvalidCastException`.

Catch order must be most-specific first; unreachable catches are diagnostics.
The caught value exposes at least `message` and `source_location`. Unhandled
Vela exceptions retain their existing source-aware behavior. User-defined
`throw` and custom exception inheritance are explicitly deferred to a later
language feature.

## Async / await and cancellation

Async is the only feature in this document that changes how user functions are
scheduled. It follows the prior foundations rather than being implemented as an
ad-hoc wrapper over synchronous APIs.

```vela
async fn fetch_profile(url: Text, cancellation: Cancellation) -> Text {
    let response = await http.get(url, cancellation);
    return response.text();
}
```

- The annotation on `async fn` names its eventual result `T`; its callable type
  is `Future<T>` and must be consumed with `await` or passed to an async API.
- `await` is legal only within `async fn`.
- `Cancellation` is created by `vela.concurrent` and is explicitly passed into
  bounded I/O operations.
- `vela.core.tcp` gains async variants; `vela.std.http` uses the same model.
- Cancellation becomes a source-aware Vela runtime failure that can be caught.

Native AOT compatibility and trimming are mandatory acceptance criteria. The
compiler will emit C# async state machines only for functions that use `async`;
synchronous programs retain their current output shape and size budget.

## Initial standard packages

The first packages are source-linked Vela packages and have no heavyweight
native dependency:

| Package | Responsibility | Core dependencies |
| --- | --- | --- |
| `vela.std.cli` | arguments, subcommands, help, progress | `env`, `text` |
| `vela.std.log` | levels, structured fields, text/JSON output | `time`, `json` |
| `vela.std.config` | profiles, environment overlays, JSON configuration | `env`, `io`, `json` |
| `vela.std.test` | assertions, fixtures, parameterized tests | `text`, `time` |

After async support (implemented first baseline):

| Package | Responsibility | Core dependencies |
| --- | --- | --- |
| `vela.std.http` | bounded plaintext HTTP/1.1 GET helper and JSON canonicalization | `tcp`, `json` |
| `vela.concurrent` | cancellation, channels, worker coordination | `time` |

Later optional packages are `vela.data.sqlite`, `vela.std.web`,
`vela.std.auth`, `vela.std.serialize`, and `vela.std.template`. They remain
optional so minimal command-line applications do not acquire database, TLS, or
web-server weight.

## Delivery order

1. Comments, documentation trivia, diagnostics, public documentation metadata,
   and fixtures for IDE consumers.
2. Enums plus enum-aware exhaustive switch and definite-return analysis.
3. `defer` lowering and cleanup tests across return, loop control, and failures.
4. `try` / `catch` / `finally`, runtime error bindings, and diagnostics.
5. Source-linked `vela.std` package resolution with the initial `cli`, `log`,
   `config`, and `test` packages. JSON use is routed through `vela.core.json`.
6. Async / await, cancellation, asynchronous TCP, and `vela.std.http`.
7. Separate LSP and editor extension projects using the metadata created in step
   1; they are intentionally not compiler prerequisites.

## Validation

- Lexer tests cover line, nested block, documentation, and unterminated comment
  spans without changing existing token positions.
- Parser and binder tests cover doc attachment, tag diagnostics, enums, all
  exhaustive-switch paths, deferred evaluation order, and legal/illegal catches.
- Backend integration tests compile and execute cleanup and exception examples.
- Standard-package tests prove JSON calls are emitted through `VelaJson`, with
  no separate JSON parsing implementation.
- Native AOT smoke tests run on each host target; baseline and selected-package
  bundles are measured against the 3 MiB budget.
- Future LSP fixture tests consume comments, symbols, hover text, and diagnostic
  ranges from the compiler's public metadata.
