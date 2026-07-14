# Vela

Vela is an experimental, modern programming language and compiler written in C#.
It combines indentation-based blocks with explicit types, generic records, managed
memory, and native Windows executable publishing. Its syntax is intentionally its
own: it borrows familiar ideas without trying to be source-compatible with C#.

## Highlights

- Immutable `let` bindings and mutable `var` bindings.
- Indentation-based `fn` and `record Name<T>:` declarations.
- Generic functions and records with managed `List<T>`, `Option<T>`, and `Result<T, E>` runtime values.
- Source-aware diagnostics with error codes, locations, excerpts, and guidance.
- Managed .NET object lifetime with garbage collection and no raw pointers or manual freeing.
- A compiler pipeline that can generate C# source and publish a self-contained
  Windows executable.

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
- Windows when publishing a self-contained `.exe`.

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

Publish a self-contained Windows executable:

```powershell
dotnet run --project .\src\Vela.Cli -- build .\examples\hello.vela `
  --target win-x64 --output .\dist\hello
```

The published executable is `dist\hello\hello.exe` and can run without a
separately installed .NET runtime.

## Language guide and examples

Read [the Vela language guide](docs/Vela-language.md) for the syntax and semantic
rules. The [examples](examples) directory contains runnable programs for a basic
entry point, generic records, managed allocations, and compiler diagnostics.

## Status

Vela is an educational compiler project. The specification and examples are kept
close to the implementation so that new features have an executable, testable
definition.
