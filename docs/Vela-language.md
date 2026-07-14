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
types such as `Vector<T>`, `HashMap<K, V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`,
`RingBuffer<T>`, `Option<T>`, and `Result<T, E>`. `List<T>` remains a
type-equivalent compatibility alias for `Vector<T>`. Identifiers are
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
indexing, collection construction, and record construction. The final expression
in a function is its returned value when no earlier return value is produced.

```vela
fn square(value: Int) -> Int:
    value * value
```

Collections are mutable values even when their binding uses `let`; `let` only
prevents rebinding the variable itself. Indexing is available for vectors and
hash maps. Index reads are bounds-checked, and a missing hash-map key through
indexing raises a managed key-not-found exception. Use `try_get` when absence is
an expected condition.

```vela
var values = Vector<Int>(16)
values.append(7)
values[0] = 9

var scores = HashMap<Text, Int>(16)
scores["Ada"] = 42
let score = scores.try_get("Ada")
if score.has_value:
    print(score.value)
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

`for` iterates vectors, hash sets, queues, stacks, and ring buffers. The loop
variable is immutable and scoped to the loop body.

```vela
for value in values:
    print(value)
```

## Contracts with `assert`

`assert` makes an executable requirement explicit. It accepts a Boolean
condition and an optional `Text` message. A failed assertion throws a managed
`VelaContractException`, and generated source retains line mapping to the
original `.vela` file.

```vela
fn divide(total: Int, parts: Int) -> Int:
    assert parts != 0, "parts must not be zero"
    total / parts
```

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

Vela uses managed memory: `Text`, collections, records, `Option<T>`, and
`Result<T, E>` values remain alive while they are reachable. Program code does
not expose raw pointers or manual freeing. Generated applications use the .NET
garbage collector, while `Int`, `Float`, and `Bool` remain efficient value types
where possible.

```vela
record Note:
    text: Text

let notes: Vector<Note> = [Note("Managed by .NET")]
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

`vela build` type-checks the source, writes generated C# into a temporary
staging directory, and invokes the .NET publishing toolchain. The default
`--target auto` uses the current host runtime identifier. A build command looks
like this:

```powershell
vela build examples/hello.vela --output dist/hello
```

It writes the primary executable directly to `dist/hello`: `hello.exe` on
Windows, or `hello` on Linux and macOS. The CLI prints the absolute artifact
path. Use `vela targets` to inspect the auto target, or `--target <rid>` to
request another .NET runtime identifier. See [the collection guide](Vela-collections.md)
for the collection API and complexity contracts.
