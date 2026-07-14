# Vela language guide

## Design in one page

Vela uses indentation to form blocks, `fn` for functions, and explicit type
annotations where they improve clarity. A newline ends a statement; semicolons
are not part of the language. Comments begin with `#` and continue to the end of
the line.

```vela
# A program exits with an integer status code.
fn main() -> Int:
    let message: Text = "Hello, Vela!"
    print(message)
    0
```

The current core types are `Int`, `Float`, `Bool`, `Text`, `Unit`, and generic
types such as `List<T>`, `Option<T>`, and `Result<T, E>`. Identifiers are
case-sensitive. Type names use PascalCase and values use camelCase by convention.

## Bindings and expressions

`let` creates an immutable binding. `var` creates a mutable binding and is the
only form that can be assigned again.

```vela
let title: Text = "Vela"
var attempts: Int = 0
attempts = attempts + 1
```

Expressions include literals, arithmetic, comparisons, calls, member access,
and record construction. The final expression in a function is its returned
value when no earlier return value is produced.

```vela
fn square(value: Int) -> Int:
    value * value
```

## Functions and flow

A function begins with `fn`, has typed parameters when required, and declares
its return type after `->`. Blocks always have a trailing `:` and their contents
must be indented more than the header.

```vela
fn max(left: Int, right: Int) -> Int:
    if left > right:
        left
    else:
        right
```

The compiler rejects inconsistent indentation, missing block bodies, and code
after a returned value when it is provably unreachable.

## Generic records and functions

Records are declared with `record Name<T>:`. Generic parameters are compile-time
type variables; every use must be consistent with its inferred or explicit type.

```vela
record Pair<T>:
    left: T
    right: T

fn first<T>(pair: Pair<T>) -> T:
    pair.left
```

Generic values can be constructed using the type arguments that make their
representation unambiguous:

```vela
let pair = Pair<Int>(7, 11)
let firstValue: Int = first(pair)
```

## Managed memory

Vela uses managed memory: `Text`, `List<T>`, records, `Option<T>`, and
`Result<T, E>` values remain alive while they are reachable. Program code does
not expose raw pointers or manual freeing. Generated applications use the .NET
garbage collector, while `Int`, `Float`, and `Bool` remain efficient value types
where possible.

```vela
record Note:
    text: Text

let notes: List<Note> = [Note("Managed by .NET")]
```

The runtime includes AOT-safe `Option<T>`, `Result<T, E>`, contracts, and
deterministic-disposal helpers for generated interop code. Source-level resource
syntax is reserved for a later Vela language version, so current programs never
pretend to manually own or free a handle.

## Diagnostics

Every compiler diagnostic has a stable error code, severity, source span, line
and column, and a source excerpt. For example, assigning to a `let` binding
should report an immutable-assignment error at the assignment target.

```text
error VEL3004: Cannot assign to immutable binding 'answer'.
 --> examples/diagnostics.vela:4:5
  |
4 |     answer = 43
  |     ^^^^^^ declared with 'let' here
help: declare the binding with 'var' if it must change.
```

Run `vela check examples/diagnostics.vela` to see this behavior. That example is
intentionally invalid and is not a runnable program.

## Executable output

`vela build` type-checks the source, writes generated C# into an intermediate
directory, and invokes the .NET publishing toolchain. A Windows build command
looks like this:

```powershell
vela build examples/hello.vela --target win-x64 --output dist/hello
```

It writes a self-contained executable to `dist/hello/hello.exe`. Intermediate
and published output directories are ignored by Git.
