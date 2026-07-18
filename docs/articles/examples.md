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
| `ui-hello-form.vela` | Windows Hello Form (`vela.core.gui`); use `VELA_UI_HEADLESS=1` in CI. |
| `ui-counter-desk.vela` | Two windows, counter, and several buttons (`run_counter_app`). |
| `ui-components.vela` | Compose forms/buttons/labels/textboxes and poll clicks. |
| `ui-onclick.vela` | Typed `on_click(fn() -> Void { ... })`, checkbox, combo, progress. |
| `ui-studio.vela` | Layouts, menus, file dialogs, list/grid, `on_text_changed`. |
| `ui-rich-controls.vela` | Textarea, slider, numeric, radio, separator, `on_value_changed`. |
| `gui-prompt.vela` | Message box and prompt dialogs on Windows. |
| `api-rest.vela` | REST server (`vela.core.http`) with GET/POST loopback. |
| `api-graphql.vela` | Minimal GraphQL mount on HTTP. |
| `api-grpc.vela` | Unary gRPC map + client call. |

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
