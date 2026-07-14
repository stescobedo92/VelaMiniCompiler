# Vela compiler design

> Historical initial-release design. Host-native publishing and collection
> behavior are superseded by
> [the host-native collections design](2026-07-14-host-native-collections-design.md).

## Scope

Vela is a small, statically checked language implemented in C# on .NET 10. The
first complete release accepts a Vela source file, produces precise diagnostics,
can run valid programs, and can publish a self-contained Windows executable. The
language favors readable indentation-based source while retaining explicit
function return types, generic records, and predictable cleanup scopes.

The supported surface includes immutable `let` bindings, mutable `var` bindings,
functions declared with `fn`, records declared with `record Name<T>:`, generic
functions, `Result<T, E>`, `requires`, `ensures`, `use` scopes, comments starting
with `#`, expressions, conditionals, calls, and basic numeric and text values.

## Repository architecture

```text
src/Vela.Core
  Text/           SourceText, positions, spans, and line maps
  Diagnostics/    Diagnostic model, rendering, and stable error identifiers
  Lexing/         Tokens, comments, indentation, and lexical errors
  Parsing/        Syntax tree and recovery-oriented parser
  Semantics/      Symbols, types, generic substitution, and binding analysis

src/Vela.Runtime
  Values/         Managed value representation and standard value operations
  Resources/      Handle abstraction and deterministic cleanup scopes
  Bytecode/       Optional compact instruction representation and virtual machine

src/Vela.Backend
  CSharp/         Lowering and generated C# source emission
  Publishing/     Validated invocation of the .NET publishing toolchain

src/Vela.Cli
  Commands/       check, run, build, and repl command handlers

tests/Vela.Core.Tests
tests/Vela.IntegrationTests
examples
docs
```

`Vela.Core` has no dependency on the CLI or publishing layer. `Vela.Runtime` is
shared by the interpreter and generated-program support. `Vela.Backend` consumes
only checked semantic models. This one-way dependency flow lets compiler stages
be tested without process execution or file publication.

## Compilation pipeline

```text
source file
  -> source text and line map
  -> tokens with indentation boundaries
  -> syntax tree
  -> bound and typed semantic model
  -> diagnostics gate
  -> interpreter or backend lowering
  -> generated C# and self-contained executable
```

Every stage receives immutable inputs and returns a result containing its output
and diagnostics. A stage may continue after an error only when recovery creates a
well-defined placeholder. The backend never runs when any error-severity
diagnostic exists.

## Syntax and semantic rules

Blocks use a header ending in `:` followed by consistently indented lines. The
lexer emits explicit indent and dedent tokens so that the parser does not depend
on raw whitespace. Tabs are rejected in source files to make block meaning
portable.

`let` introduces a binding that cannot be assigned after initialization. `var`
introduces a binding whose assignments are type-checked. Symbols use lexical
scope. Names must resolve before type checking continues, and duplicate names in
the same scope are errors.

`record Name<T>:` declares fields with their types. Functions can introduce type
parameters with `fn name<T>(...)`. Type inference can infer a generic argument
from function arguments; explicit type arguments remain available where inference
is ambiguous. Generic templates are checked once using type parameters and then
specialized for the virtual machine. The C# backend emits corresponding generic
record and method declarations when the target representation supports them.

`Result<T, E>` models expected failures. `Ok` and `Err` are validated against the
declared result type. A value cannot be used as an unrelated type merely because
it is carried by a result.

## Diagnostics

Diagnostics are structured data, not strings assembled ad hoc. Each diagnostic
contains:

- A stable identifier such as `VEL1001`.
- An error, warning, or information severity.
- A primary source span and optional labeled secondary spans.
- A concise message and an optional actionable help message.
- A renderer that uses the source line map to show one-based line and column
  positions with a caret excerpt.

The initial identifier ranges are `VEL1xxx` for lexing, `VEL2xxx` for parsing,
`VEL3xxx` for binding and types, `VEL4xxx` for function obligations and cleanup,
and `VEL9xxx` for command or publishing failures. Parsing uses synchronization at
line boundaries and closing delimiters so a single file can report multiple
independent errors.

## Managed memory and external handles

Vela values are represented by managed .NET objects or value types. The runtime
relies on the .NET garbage collector for unreachable values; generated Vela code
does not expose pointer arithmetic or manual deallocation.

`use name = expression:` establishes deterministic cleanup for a value that
conforms to the runtime handle protocol. Lowering turns this construct into a
`try`/`finally` equivalent so cleanup occurs on normal completion, an early
function result, or an error path. Nested cleanup scopes run in reverse order.
The semantic analyzer reports a diagnostic when the expression cannot produce a
managed handle.

`requires` establishes a precondition for a function invocation. `ensures`
establishes a condition for a successful function result. Constant and
statically provable conditions are checked during semantic analysis; all other
conditions are emitted as runtime checks with their source spans attached to a
failure diagnostic. These clauses must be the first non-blank statements in a
function body. The implicit `result` name is valid only in an `ensures` clause
and refers to that function's returned value.

## Output and publishing

The build command is:

```text
vela build <input.vela> --target win-x64 --output <directory>
```

For `examples/hello.vela` with `--output dist/hello`, the compiler writes
generated source under an intermediate `obj` directory, creates a temporary .NET
project with pinned local settings, and invokes `dotnet publish` for `win-x64`
with self-contained and single-file publishing enabled. The successful artifact
is `dist/hello/hello.exe`. The command validates the target and output directory,
forwards compiler failures as `VEL9xxx` diagnostics, and does not leave a partial
executable in the requested output directory on failure.

Generated source is retained only with an explicit diagnostic or debug option;
normal build output is deterministic and Git ignores intermediate and published
directories.

## Verification strategy

Unit tests cover source line mapping, tokenization, indentation, parser recovery,
operator precedence, scopes, binding mutability, type inference, generic
substitution, result typing, function obligations, and cleanup ordering.

Integration tests compile every valid file in `examples`, execute the hello and
generic examples, assert the expected diagnostic code and location for the
intentional failure, and publish then execute a `win-x64` artifact on Windows.
Golden diagnostic tests compare rendered messages, spans, source excerpts, and
help text. A release is accepted only when `dotnet build Vela.slnx` and
`dotnet test Vela.slnx` succeed with zero warnings and errors.

## Non-goals

- Source compatibility with C# or any other existing language.
- Unsafe pointers, manual allocation, or manual deallocation.
- A full standard library, package manager, debugger, or language server in the
  first release.
- Cross-platform self-contained artifacts before the Windows publishing path is
  fully covered by integration tests.
- Continuing to publish after an error-severity compiler diagnostic.
