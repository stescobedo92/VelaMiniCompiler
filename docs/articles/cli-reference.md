---
title: Compiler CLI reference
---

# Compiler CLI reference

The CLI is phase-oriented and verbose by default. In an interactive terminal it
uses color; redirected output, `NO_COLOR=1`, or `--color never` disables it.

| Command | Purpose |
| --- | --- |
| `vela check <input>` | Parse, bind, and validate without producing an artifact. |
| `vela run <input> [-- args]` | Compile a temporary project and run it with only the explicit program arguments. |
| `vela build <input>` | Produce a host-native executable by default. |
| `vela targets` | Show the resolved host runtime identifier. |

`<input>` may be a `.vela` file, a package directory, or its `vela.toml`.

## Common options

- `-q`, `--quiet`: suppress normal phase messages.
- `-v`, `--verbose`: increase detail; `-vv` includes raw .NET publish output.
- `--color auto|always|never`: control ANSI output.
- `--output <directory>`: choose the final artifact directory.
- `--target <rid>`: publish for an explicit runtime identifier.
- `--mode native-aot|single-file|framework-dependent`: choose emission mode.
- `--lib`: build a native shared library and ABI manifest.

## Build phases

A typical build reports `Resolving`, `Including`, `Checking`, `Compiling`,
`Lowering`, `Publishing`, and `Finished`. Diagnostics always include a stable
code, source file, line/column, excerpt, caret span, and actionable help when
available.

## Exit codes

- `0`: command and compiled program succeeded.
- `1`: source compilation failed, or the executed Vela program returned 1.
- `2`: invalid CLI/package usage, or the executed program returned 2.
- Other `run` exit codes are returned from the Vela program unchanged.
