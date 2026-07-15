---
title: Language tour
---

# Language tour

## Values and returns

Supported scalar types are `Int`, `UInt`, `Long`, `Float`, `Double`, `Decimal`,
`Bool`, `Text` (`String` alias), and `Any`. `Void` is valid only as a function or
method return type. The legacy spelling `Unit` is accepted with warning
`VELW001` and should not appear in new source.

```vela
fn notify(message: Text) -> Void {
    print(message);
}
```

Arithmetic conversions are explicit. Integer and decimal arithmetic is checked;
non-finite floating results become Vela arithmetic errors.

## Classes and interfaces

Every class declaration includes a primary constructor list, even when empty.
Constructor parameters are in scope for field initializers and methods. Every
class field must be initialized.

```vela
interface Named { fn name() -> Text; }
interface Resettable { fn reset() -> Void; }

class Worker(worker_name: Text, initial: Int = 0) implements Named, Resettable {
    var current: Int = initial;
    fn name() -> Text { return worker_name; }
    fn reset() -> Void { self.current = 0; }
}
```

The compiler rejects duplicate/unknown interfaces and checks each method name,
generic arity, parameter type, return type, and async contract.

## Named and default arguments

```vela
fn connect(host: Text, port: Int = 8080, timeout_ms: Int = 5000) -> Void { }

connect("localhost");
connect("localhost", timeout_ms: 1000);
connect(timeout_ms: 2500, host: "127.0.0.1", port: 9000);
```

Required parameters precede optional ones. Positional arguments precede named
ones. Every supplied expression is evaluated once; a default may reference an
earlier parameter and is evaluated at the call site.

## Option, Result, and match

Factories are explicit and fully typed:

```vela
let value = some<Int>(42);
let missing = none<Int>();
let success = ok<Int, Text>(42);
let failure = err<Int, Text>("unavailable");

match success {
    case Ok(number) { print(number); }
    case Err(message) { print(message); }
}
```

`match` also supports enums and exact primitive literals. Enum, `Option`, and
`Result` matches must cover every variant or end with `default`. Duplicate and
unreachable cases are errors.

## Destructuring and metadata

```vela
let (host, port) = ("localhost", 8080);
let ServerConfig { name, timeout_ms } = config;
```

Tuple and record destructuring is immutable, one level deep, and exact. `_`
discards a value.

Attributes carry compile-time metadata without reflection:

```vela
@since("0.2.0")
@experimental
class Command(name: Text) {
    @deprecated("Use run instead.")
    fn execute() -> Void { }
}
```

Unknown attributes and invalid arguments are errors. Uses of deprecated and
experimental declarations produce `VELW002` and `VELW003` warnings.

See the complete [language reference](../Vela-language.md) for control flow,
exceptions, `defer`, async functions, comments, and FFI rules.
