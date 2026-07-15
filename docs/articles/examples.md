---
title: Examples
---

# Examples

Run a single-file example with:

```powershell
dotnet run --project src/Vela.Cli -- run examples/void-functions.vela
```

| Example | Demonstrates |
| --- | --- |
| `void-functions.vela` | `Void` work functions and console output. |
| `primary-constructors.vela` | Constructor parameters, defaults, initialized fields. |
| `multiple-interfaces.vela` | Three independently checked interface contracts. |
| `match-option-result.vela` | Algebraic factories and exhaustive matches. |
| `named-default-arguments.vela` | Named calls and defaults referencing earlier parameters. |
| `destructuring.vela` | Tuple and exact record destructuring. |
| `attributes.vela` | Version, experimental, deprecated, and hidden metadata. |
| `system-exec.vela` | Cross-platform direct process execution with bounds. |
| `io-workspace.vela` | Temporary workspace, deterministic files, and cleanup. |

Package applications:

```powershell
dotnet run --project src/Vela.Cli -- run examples/packages/vela-cli-app -- --name Ada --json
dotnet run --project src/Vela.Cli -- run examples/packages/vela-logging-app
dotnet run --project src/Vela.Cli -- run examples/packages/vela-server-tool
```

The server tool combines CLI parsing, structured logs, `system.exec`, file I/O,
bounded output, and deterministic cleanup. It runs only the trusted local
`dotnet` executable discovered on `PATH` and writes only below a temporary
directory.
