# Vela

Vela is an experimental, modern programming language and compiler written in C#.
It combines indentation-based blocks with explicit types, generic records, managed
memory, and host-native executable publishing. Its syntax is intentionally its
own: it borrows familiar ideas without trying to be source-compatible with C#.

## Highlights

- Immutable `let` bindings and mutable `var` bindings.
- Indentation-based `fn` and `record Name<T>:` declarations.
- Generic functions and records with managed `Vector<T>`, `HashMap<K, V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `RingBuffer<T>`, `BitSet`, `Option<T>`, and `Result<T, E>` runtime values.
- Source-aware diagnostics with error codes, locations, excerpts, and guidance.
- Managed .NET object lifetime with garbage collection and no raw pointers or manual freeing.
- Source-level assertions backed by the managed contract runtime.
- A compiler pipeline that can generate C# source and publish a host-native
  executable automatically.

## Repository layout

```text
src/
  Vela.Core/       Source text, diagnostics, lexing, parsing, AST, and semantics
  Vela.Runtime/    Managed values, contracts, and deterministic disposal helpers
  Vela.Backend/    C# source generation and publishing integration
  Vela.Cli/        Command-line compiler, runner, and REPL
tests/
  Vela.Core.Tests/ Unit and diagnostic tests
  Vela.IntegrationTests/ End-to-end compiler and executable tests
examples/          Small Vela programs, including one intentional failure
docs/              Language and engineering documentation
```

## Prerequisites

- .NET SDK 10.0 or later.
- A host supported by the installed .NET Native AOT toolchain when publishing a native executable.

## Build and test

```powershell
dotnet build Vela.slnx
dotnet test Vela.slnx
```

## Use the compiler

Run a program during development:

```powershell
dotnet run --project .\src\Vela.Cli -- run .\examples\hello.vela
```

Check a program without creating output files:

```powershell
dotnet run --project .\src\Vela.Cli -- check .\examples\diagnostics.vela
```

`diagnostics.vela` is deliberately invalid and should produce a readable error.

Publish a host-native executable. `--target auto` is the default and selects the
current .NET runtime identifier without parsing it:

```powershell
dotnet run --project .\src\Vela.Cli -- build .\examples\hello.vela `
  --output .\dist\hello
```

On Windows the artifact is `dist\hello\hello.exe`; on Linux and macOS it is
`dist/hello/hello`. The command prints the final absolute path as `Executable:`.
Use `vela targets` to display the auto target, or use `--target <rid>` to make
an explicit publishing request.

## Language guide and examples

Read [the Vela language guide](docs/Vela-language.md) for the syntax and semantic
rules. The [examples](examples) directory contains runnable programs for a basic
entry point, generic records, managed allocations, contract assertions,
collections, and compiler diagnostics. Read [the collection guide](docs/Vela-collections.md)
for APIs and precise complexity guarantees.

## Status

Vela is an educational compiler project. The specification and examples are kept
close to the implementation so that new features have an executable, testable
definition.
