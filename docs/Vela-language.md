# Vela language guide

Vela uses braces for blocks and semicolons for simple statements. The language
is statically checked and lowers to C# plus the Vela runtime before Native AOT
publishing. Whitespace improves readability but does not change program meaning.

## Minimal program

```vela
include vela.core;

fn main() -> Void {
    let message: Text = "Hello, Vela!";
    print(message);
}
```

Every source file that uses core language services starts with
`include vela.core;`. `main` may return `Int` as a process exit code or `Void`
for an implicit successful exit. `Void` is legal only as a return type; the
legacy source spelling `Unit` produces warning `VELW001`.

## Bindings, values, and conversion

`let` creates an immutable binding and `var` creates a mutable binding. Available
numeric types are `Int`, `UInt`, `Long`, `Float`, `Double`, and `Decimal`.
`Text` is the canonical string type; `String` is an alias. `Bool`, `Any`,
`Array<T>`, `Option<T>`, `Result<T, E>`, and generic collection types are also
available. There is no `UFloat` type.

```vela
let title: Text = "Vela";
var count: Int = 1;
count = count + 1;

let total: Long = Long(count);
let decimalTotal: Decimal = Decimal(total);
let ratio: Double = Double(decimalTotal);
```

Integer, decimal, and conversion overflow checks are explicit runtime operations.
Division by zero, non-finite floating results, invalid conversion, and invalid
array access report a Vela runtime exception with the originating source location.

## Functions and control flow

Functions use `fn`, typed parameters, and an optional return type. Statements in
blocks terminate with `;`; declarations and control-flow bodies use `{` and `}`.

```vela
fn factorial(value: Int) -> Int {
    assert value >= 0, "factorial requires a non-negative Int";
    if value == 0 {
        return 1;
    } else {
        return value * factorial(value - 1);
    }
}
```

Required parameters precede optional parameters. Defaults run at the call site,
may reference earlier parameters, and every argument expression is evaluated
exactly once. Positional arguments precede named arguments.

```vela
fn connect(host: Text, port: Int = 8080, timeout_ms: Int = 5000) -> Void { }

connect("localhost");
connect("localhost", timeout_ms: 1000);
connect(timeout_ms: 2500, host: "localhost", port: 9000);
```

`if`/`else` and `for` use the same brace model:

```vela
for value in values {
    print(value);
}
```

`while`, `break`, `continue`, and `switch` add compact imperative control flow.
`break` and `continue` apply only to the nearest `while` or `for`. Switch cases
are isolated brace blocks; they do not fall through and do not require `break`.

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
    case 8 {
        print("stopped");
    }
    default {
        print("other");
    }
}
```

Switch subjects and literal cases support `Int`, `UInt`, `Long`, `Bool`, and
`Text`. The compiler rejects duplicate case values and multiple `default` blocks.
`enum` values are strongly typed and require an exhaustive switch when no
`default` branch is present:

```vela
enum State { Ready, Running, Failed }

fn exit_code(state: State) -> Int {
    switch state {
        case State.Ready { return 0; }
        case State.Running { return 1; }
        case State.Failed { return 2; }
    }
}
```

`match` adds exhaustive variant patterns for enums, `Option<T>`, and
`Result<T,E>`, plus exact primitive literal cases. Use `some<T>`, `none<T>`,
`ok<T,E>`, and `err<T,E>` to construct algebraic values.

```vela
let result = ok<Int, Text>(42);
match result {
    case Ok(value) { print(value); }
    case Err(error) { print(error); }
}
```

## Comments, documentation, cleanup, and exceptions

Vela accepts regular line comments (`//`) and nested block comments
(`/* ... */`). Documentation comments (`///` and `/** ... */`) attach to the
next declaration when separated only by whitespace. They preserve source spans
for future editor tooling and support `@param`, `@returns`, `@throws`,
`@example`, and `@deprecated` tags.

Declaration attributes `@since("version")`, `@deprecated("message")`,
`@experimental`, and `@doc(hidden)` are validated metadata for documentation and
future IDE integrations. Deprecated and experimental uses emit warnings without
runtime reflection.

`defer` evaluates its call target and arguments immediately, then executes the
call in last-in-first-out order when its lexical block exits. It runs for normal
completion, `return`, loop control, and exceptions.

```vela
include vela.core.tcp as tcp;

fn send(payload: Text) -> Int {
    let connection = tcp.connect("127.0.0.1", 7007, 1000);
    defer tcp.close(connection);
    tcp.send_text(connection, payload);
    return 0;
}
```

Use typed handlers for predictable runtime failures. Handlers must be ordered
from specific to general; caught errors expose `message` and
`source_location`.

```vela
include vela.core.io as io;

try {
    let payload = io.read_text("config.json");
    print(payload);
}
catch VelaIoException error {
    print(error.message);
}
finally {
    print("configuration read completed");
}
```

## Async functions and cancellation

`async fn` declares its eventual result type, while callers observe a
`Future<T>`. `await` is legal only within an async function. `Cancellation` is
created explicitly through `vela.concurrent` and passed to asynchronous I/O.

```vela
include vela.core.tcp as tcp;
include vela.concurrent as concurrent;

async fn fetch() -> Text {
    let cancellation = concurrent.create();
    let connection = await tcp.connect_async("127.0.0.1", 8080, 1000, cancellation);
    defer tcp.close(connection);
    await tcp.send_text_async(connection, "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n", cancellation);
    return await tcp.receive_text_async(connection, 4096, cancellation);
}
```

## Classes, structs, interfaces, records, and generics

Vela supports reference classes, value structs, interfaces, records, generic
functions, and generic record/object types. Instance members use `self`.

```vela
interface Printable {
    fn render() -> Text;
}

interface Resettable {
    fn reset() -> Void;
}

class Counter(start: Int) implements Printable, Resettable {
    var value: Int = start;

    fn increment() -> Int {
        self.value = self.value + 1;
        return self.value;
    }

    fn render() -> Text {
        return "Counter";
    }

    fn reset() -> Void {
        self.value = 0;
    }
}
```

Every class declares its constructor list at class level (`class Empty()`) and
every class field is initialized. Constructor parameters remain available to
field initializers and methods. The compiler merges and verifies every listed
interface contract. Struct construction remains field-based.

Tuples contain two through eight fixed elements. Tuple and record destructuring
creates immutable bindings, requires exact arity/fields, and allows `_` as a
discard.

```vela
let (host, port) = ("localhost", 8080);
let ServerConfig { name, timeout_ms } = config;
```

Members are package-internal by default. Prefix a top-level declaration with
`public` when it is part of a package API. `public ffi fn` marks a native shared
library export.

## Nullability, arrays, and boxing

`T?` is Vela's explicit optional value representation. `null` is legal only for
an optional target. Reading `.value` when no value exists throws a
`VelaNullReferenceException`.

```vela
let boxed: Any = 42;
let number: Int = unbox<Int>(boxed);
let maybe: Int? = try_unbox<Int>(boxed);

var values = Array<Int>(2);
values[0] = number;
print(values[0]);
```

Assignments to `Any` box value types. `unbox<T>` validates the exact Vela type
and fails with `VelaInvalidCastException`; `try_unbox<T>` returns `T?` instead.
All array access is bounds-checked.

## Packages, source libraries, and native libraries

A package has a deterministic `vela.toml` and an optional local dependency list.

```toml
[package]
name = "vela.app"
version = "0.1.0"
kind = "application"

[dependencies]
vela.math = { path = "../vela-math" }
```

Source imports use the package name and an optional alias:

```vela
include vela.core;
include vela.math as math;

fn main() -> Int {
    print(math.add(40, 2));
    return 0;
}
```

`vela build --lib` emits a platform-native shared library and a
`.velaabi.json` manifest. The current cross-package importer supports scalar
`Bool`, `Int`, `UInt`, `Long`, `Float`, `Double`, and `Void` signatures. Text,
decimal, objects, arrays, and collections are intentionally not passed across
the ABI boundary yet.

Use `kind = "source-library"` for Vela libraries that expose rich Vela types.
The compiler links their `src/lib.vela` source into the consuming application,
preserves source locations, and emits no dependency DLL/SO. APIs are called
through the import alias; a source package named `vela.std.config` exports
`config_get_text` as `config.get_text(...)`.

```toml
[package]
name = "vela.std.config"
version = "0.1.0"
kind = "source-library"
```

## Explicit core modules

Core modules are opt-in imports and are trimmed from programs that do not call
them. Their aliases default to the final module name.

| Import | Representative operations |
| --- | --- |
| `vela.core.json` | `is_valid`, `compact`, `pretty`, `quote`, `try_get_text/int/bool` |
| `vela.core.crypto` | `sha256`, `hmac_sha256`, fixed-time comparison, random hex |
| `vela.core.tcp` | bounded synchronous connect, send, receive, close |
| `vela.core.text` | search, trim, casing, parsing/formatting, Unicode scalar creation |
| `vela.core.math` | `abs`, `min`, `max`, `clamp`, `sqrt`, `pow` |
| `vela.core.time` | UTC milliseconds and monotonic timing |
| `vela.core.random` | fast integer and double random values |
| `vela.core.io` | files/directories, deterministic listing, paths, temporary workspaces |
| `vela.core.encoding` | UTF-8 byte count, hexadecimal and Base64 conversion |
| `vela.core.env` | environment variables, process arguments, current directory |
| `vela.core.system` | direct bounded process execution, executable lookup, process metadata |
| `vela.core.console` | stdout/stderr output, redirection and color capability |
| `vela.core.gui` | Windows desktop UI: dialogs, demos, and component API (`create_form`, `add_button`, `was_clicked`, …) |
| `vela.concurrent` | explicit cancellation creation, request, and state |

```vela
include vela.core.json;
include vela.core.crypto;

fn main() -> Int {
    let payload = json.compact("{ \"id\": 42 }");
    let signature = crypto.hmac_sha256("secret", payload);
    print(signature);
    return 0;
}
```

Network and file failures are reported as source-aware Vela runtime exceptions.
TCP offers bounded synchronous calls plus `connect_async`, `send_text_async`,
and `receive_text_async`; async operations require a `Cancellation` handle.

## Diagnostics and build output

`vela check` reports source locations, codes, snippets, and colored syntax in
interactive terminals. `vela build` prints phase-level progress by default.
Use `--quiet` to suppress normal progress, `--color never` for plain output, and
`-vv` to reveal raw .NET Native AOT publishing output.

```powershell
dotnet run --project .\src\Vela.Cli -- check .\examples\factorial.vela
dotnet run --project .\src\Vela.Cli -- build .\examples\factorial.vela --output .\artifacts\factorial
dotnet run --project .\src\Vela.Cli -- run .\examples\packages\vela-cli-app -- --name Ada
```

The compiler selects the host runtime identifier by default. Use `vela targets`
to inspect it or `--target <rid>` for an explicit supported Native AOT target.
