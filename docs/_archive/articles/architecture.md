---
title: Architecture and performance
---

# Architecture and performance

```text
Vela source
  -> source-aware lexer and parser
  -> syntax tree + diagnostics
  -> semantic checks and typed lowering
  -> deterministic C# source
  -> .NET Native AOT publish
  -> host executable or shared library
```

`Vela.Core` owns text, diagnostics, tokens, parsing, and syntax. `Vela.Backend`
binds names/types, validates control flow and contracts, emits C#, resolves
packages, and invokes the toolchain. `Vela.Runtime` supplies checked primitives,
collections, I/O, networking, and Native-AOT-safe support. `Vela.Cli` renders
the user-facing phase log and diagnostics.

## Performance model

- `List`/vector and array indexing: O(1).
- `HashMap` and `HashSet` lookup/insert/remove: expected O(1).
- Queue enqueue/dequeue and stack push/pop: amortized O(1).
- Directory listings: O(n log n) intentionally, to guarantee deterministic
  ordinal order.
- CLI option/subcommand lookup: expected O(1) after definition.
- Process output: O(n) up to the explicit byte cap; stdout/stderr are consumed
  concurrently to prevent pipe deadlock.

Default argument calls stay direct when every argument is supplied. When a
default is used, generated temporaries enforce exactly-once evaluation and
preserve references to earlier parameters.

## Native size discipline

Modules and source packages are opt-in. The runtime avoids reflection-heavy
serialization and dynamic loading paths. Native AOT trimming removes unused
code, and release reporting distinguishes the primary executable size from the
complete distribution bundle. The current goal is a sub-3 MiB executable where
the selected runtime surface permits it; the compiler reports, rather than
hides, a bundle that exceeds that budget.

## Reproducibility

The repository and every generated temporary project use the pinned SDK from
`global.json`. Local package manifests produce a deterministic lock file. CI
runs each test project independently so a failed project cannot be masked by an
aggregate solution runner.
