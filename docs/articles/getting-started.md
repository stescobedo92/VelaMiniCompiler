---
title: Getting started
---

# Getting started

## Prerequisites

- .NET SDK 10.0.201, selected by the repository `global.json`.
- A Native AOT toolchain for native publishing on the host platform.
- PowerShell, Bash, or another terminal capable of invoking `dotnet`.

Restore and verify the compiler:

```powershell
dotnet restore Vela.slnx
dotnet build Vela.slnx --configuration Release --no-restore
dotnet test Vela.slnx --configuration Release --no-build --no-restore
```

## Create a program

Save this as `hello.vela`:

```vela
include vela.core.console as console;

fn main() -> Void {
    console.write_line("Hello from Vela");
}
```

Check it without producing an artifact:

```powershell
dotnet run --project src/Vela.Cli -- check hello.vela
```

Run it through a temporary framework-dependent project:

```powershell
dotnet run --project src/Vela.Cli -- run hello.vela
```

Publish the native executable:

```powershell
dotnet run --project src/Vela.Cli -- build hello.vela --output artifacts/hello
```

The result is `artifacts/hello/hello.exe` on Windows and
`artifacts/hello/hello` on Linux or macOS.

## Pass arguments through `vela run`

Arguments following `--` belong to the Vela program, not the compiler:

```powershell
dotnet run --project src/Vela.Cli -- run examples/packages/vela-cli-app -- --name Ada --json
```

## Create a local package

```text
my-app/
  vela.toml
  src/
    main.vela
```

```toml
[package]
name = "my.app"
version = "0.1.0"
kind = "application"

[dependencies]
vela.std.log = { path = "../packages/vela.std.log" }
```

Vela resolves the local graph, verifies names and versions, rejects cycles, and
writes a deterministic `vela.lock`. A `source-library` is compiled together
with the application; a `library` produces a native shared library and ABI
manifest.
