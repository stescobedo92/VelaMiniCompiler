# Core modules, control flow, and terminal color design

## Goal

This increment makes Vela's interactive compiler output reliably colorized and
adds a compact, explicitly imported core-module surface. It also adds familiar
brace-based control flow: `while`, `break`, `continue`, and `switch`.

The design preserves the current Native AOT baseline: a module is represented by
an emitter intrinsic and a small runtime helper, and is reachable only from a
program that imports and calls it. Native AOT trimming therefore keeps unused
module code out of a normal Vela executable.

## Terminal color policy

The CLI retains `--color auto|always|never`.

- `always` configures Spectre.Console for TrueColor regardless of output
  detection.
- `never` configures no colors.
- `auto` configures TrueColor when standard output is interactive, `NO_COLOR` is
  not set, and `TERM` is not `dumb`; otherwise it configures no colors.

Compiler events use stable semantic colors: checking and resolution in cyan,
compilation/lowering in blue, successful output in green, targets and sizes in
magenta, warnings in yellow, and errors in red. Source diagnostics retain
syntax-aware snippets. Program output is never modified by the compiler.

## Core module model

Source declares module dependencies explicitly:

```vela
include vela.core.json;
include vela.core.crypto;
include vela.core.tcp;
```

The default alias is the final package segment (`json`, `crypto`, `tcp`). An
`as` alias is legal. The compiler recognizes exactly the modules in the table
below. Unknown `vela.core.*` modules report an error rather than silently
falling back to a package dependency.

| Module | Initial Vela operations | Runtime and performance contract |
| --- | --- | --- |
| `vela.core.json` | `is_valid`, `compact`, `pretty`, `try_get_text`, `try_get_int`, `try_get_bool` | Uses `System.Text.Json`; property queries parse once per call and return `Option<T>` for absence/type mismatch. No reflection-based object serialization. |
| `vela.core.crypto` | `sha256`, `hmac_sha256`, `constant_time_equals`, `random_hex` | Uses static hashing APIs and `RandomNumberGenerator`; hashes are lower-case hexadecimal `Text`; comparison is fixed-time for equal-length byte sequences. |
| `vela.core.tcp` | `connect`, `send_text`, `receive_text`, `close` | Uses a blocking `TcpClient` wrapper with explicit connect/read timeouts and bounded receive size. The opaque `TcpConnection` is disposable and never crosses a native ABI boundary. |
| `vela.core.text` | `length`, `contains`, `starts_with`, `ends_with`, `trim`, `to_upper_invariant`, `to_lower_invariant`, `slice` | All comparison/search functions use ordinal semantics. `slice` validates bounds and reports Vela source locations. |
| `vela.core.math` | `abs`, `min`, `max`, `clamp`, `sqrt`, `pow` | Numeric overload selection happens in the emitter with no boxing. Existing checked numeric rules remain in force. |
| `vela.core.time` | `utc_unix_milliseconds`, `monotonic_ticks`, `elapsed_milliseconds` | `Stopwatch.GetTimestamp` supplies monotonic measurements; UTC is supplied only for wall-clock timestamps. |
| `vela.core.random` | `next_int`, `next_double` | Uses the process-shared fast random generator. Cryptographic randomness stays in `crypto`. |
| `vela.core.io` | `exists`, `read_text`, `write_text`, `append_text` | Uses direct file APIs with UTF-8 text and source-aware Vela I/O failures. It performs no implicit directory traversal or background work. |
| `vela.core.encoding` | `utf8_byte_count`, `hex_encode`, `hex_decode`, `base64_encode`, `base64_decode` | Uses span-oriented BCL encoders where available; invalid encoded input reports a Vela runtime error. |
| `vela.core.env` | `get`, `get_or`, `argument_count`, `argument`, `current_directory` | Reads host state directly. Missing environment variables and argument indexes use `Option<T>` / bounds failures instead of fabricated values. |

`json`, `crypto`, `tcp`, `io`, and `encoding` convert host failures to typed,
source-aware Vela runtime exceptions. The initial TCP API is synchronous by
design: it is predictable, simple to test, and does not introduce an async
runtime or scheduler into Native AOT output.

## New control-flow syntax

All blocks use braces and simple statements end in semicolons.

```vela
var index: Int = 0;
while index < 10 {
    index = index + 1;
    if index == 3 {
        continue;
    }
    if index == 8 {
        break;
    }
}

switch index {
    case 0 {
        print("empty");
    }
    case 8 {
        print("stopped");
    }
    default {
        print("other");
    }
}
```

- `while` requires a `Bool` condition.
- `break` is valid only inside `while` or `for` and exits the nearest loop.
- `continue` is valid only inside `while` or `for` and advances the nearest
  loop.
- `switch` accepts `Int`, `UInt`, `Long`, `Bool`, or `Text`. Cases are constant
  literals of the subject type and may appear once each; `default` is optional
  and appears at most once.
- Vela switch cases are isolated blocks with no fall-through, so `break` is not
  required for a case. The emitter lowers them to ordered direct comparisons,
  avoiding generated C# fall-through rules while preserving predictable source
  behavior.

## Architecture

`Vela.Core` gains lexer tokens, AST nodes, parser recovery, and diagnostics for
the four control-flow forms. `Vela.Backend` gains a registry keyed by core-module
name and operation. It validates the import, arity, types, and return type before
emitting direct calls to `Vela.Runtime` helpers. `Vela.Runtime` gains focused
module classes plus `VelaIoException`, `VelaFormatException`, and
`VelaNetworkException` to preserve source locations.

`TcpConnection` is a recognized opaque Vela type. It can be inferred from
`tcp.connect` and supplied to `tcp.send_text`, `tcp.receive_text`, and
`tcp.close`, but it cannot be converted to `Any`, serialized, or used in an FFI
signature. Each native module has no static initialization or reflection path.

## Examples and tests

New checked examples under `examples/`:

1. `control-flow.vela`: simple `while`, `break`, `continue`, and `switch`.
2. `core-text-math.vela`: text, math, time, random, encoding, and environment
   calls with a small command-line report.
3. `secure-message.vela`: classes and an interface that validate JSON, build a
   canonical payload, calculate an HMAC, and compare signatures safely.
4. `tcp-echo-client.vela`: a bounded TCP client example with explicit close.
5. `file-json-report.vela`: file I/O, JSON validation/query, and a typed report
   class.

Tests cover tokenization and parser recovery, invalid loop control placement,
switch typing/duplicates/default rules, every intrinsic's arity/type validation,
runtime correctness and hostile input, color-mode selection, and framework plus
host-native integration. TCP integration starts a local loopback echo server;
it never contacts an external host. Native tests measure an empty application
and selected module examples to ensure the baseline remains below 3 MiB and
report module-specific growth explicitly.

## Non-goals

- No async/await, task scheduler, remote package registry, reflection-based JSON
  binding, HTTP client, TLS policy surface, or cross-library object handles.
- No fall-through switch cases or implicit conversion between switch types.
- No automatic coloring of user program output.
