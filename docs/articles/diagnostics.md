---
title: Diagnostics and safety
---

# Diagnostics and safety

Diagnostics are stable contracts for terminal users and future IDE/LSP clients.
They preserve exact source spans across source-linked packages.

```text
error VEL3004: Cannot assign to immutable binding 'answer'.
  --> examples/diagnostics.vela:8:5
  |
8 |     answer = 43;
  |     ^~~~~~
  = help: Declare the binding with 'var' if it must change.
```

## Productivity diagnostics

| Code | Meaning |
| --- | --- |
| `P013` | Missing class constructor list or constructor list on a non-class. |
| `P014` | Invalid parameter or named argument grammar. |
| `P015` | Invalid `match` order or pattern syntax. |
| `P016` | Invalid tuple/destructuring grammar. |
| `VEL3020` | Illegal `Void` value use or value returned from `Void`. |
| `VEL3021` | Uninitialized class field or invalid struct initializer. |
| `VEL3022` | Duplicate/unknown interface or unsatisfied contract. |
| `VEL3023` | Missing, duplicate, or unknown call argument. |
| `VEL3024` | Non-exhaustive, duplicate, or incompatible match pattern. |
| `VEL3025` | Destructuring type, arity, or field mismatch. |
| `VEL3026` | Unknown, duplicated, mistyped, or invalid attribute. |
| `VELW001` | Legacy `Unit` spelling; use `Void`. |
| `VELW002` | Use of a deprecated declaration/member. |
| `VELW003` | Use of an experimental declaration/member. |

## Runtime safety

The runtime translates host failures to source-aware Vela errors:

- `VelaOverflowException` and `VelaArithmeticException`;
- `VelaNullReferenceException` and `VelaIndexOutOfRangeException`;
- `VelaInvalidCastException` for failed unboxing;
- `VelaIoException`, `VelaNetworkException`, and `VelaProcessException`;
- `VelaFormatException`, `VelaCancellationException`, and cleanup errors.

Use `try/catch/finally` for recovery and `defer` for deterministic LIFO cleanup.
Do not use exceptions as ordinary collection lookup flow; `Option<T>` and
`Result<T,E>` keep expected absence and domain failure explicit.
