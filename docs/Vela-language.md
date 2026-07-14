# Vela language guide

Vela uses braces for blocks and semicolons for simple statements. The language
is statically checked and lowers to C# plus the Vela runtime before Native AOT
publishing. Whitespace improves readability but does not change program meaning.

## Minimal program

```vela
include vela.core;

fn main() -> Int {
    let message: Text = "Hello, Vela!";
    print(message);
    return 0;
}
```

Every source file that uses core language services starts with
`include vela.core;`. `main` returns the process exit code.

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

`if`/`else` and `for` use the same brace model:

```vela
for value in values {
    print(value);
}
```

## Classes, structs, interfaces, records, and generics

Vela supports reference classes, value structs, interfaces, records, generic
functions, and generic record/object types. Instance members use `self`.

```vela
interface Printable {
    fn render() -> Text;
}

class Counter implements Printable {
    var value: Int;

    fn increment() -> Int {
        self.value = self.value + 1;
        return self.value;
    }

    fn render() -> Text {
        return "Counter";
    }
}
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

## Packages and native libraries

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
`Bool`, `Int`, `UInt`, `Long`, `Float`, `Double`, and `Unit` signatures. Text,
decimal, objects, arrays, and collections are intentionally not passed across
the ABI boundary yet.

## Diagnostics and build output

`vela check` reports source locations, codes, snippets, and colored syntax when
the terminal supports it. `vela build` prints phase-level progress by default.
Use `--quiet` to suppress normal progress, `--color never` for plain output, and
`-vv` to reveal raw .NET Native AOT publishing output.

```powershell
dotnet run --project .\src\Vela.Cli -- check .\examples\factorial.vela
dotnet run --project .\src\Vela.Cli -- build .\examples\factorial.vela --output .\artifacts\factorial
```

The compiler selects the host runtime identifier by default. Use `vela targets`
to inspect it or `--target <rid>` for an explicit supported Native AOT target.
