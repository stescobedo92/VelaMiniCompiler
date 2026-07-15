---
title: Vela compiler
description: Native-first, statically checked, cross-platform language and compiler.
---

# Build small native programs with predictable behavior

Vela is an experimental statically checked language with familiar brace-based
syntax, checked arithmetic, source-aware runtime errors, deterministic package
resolution, and host-native output. It compiles through a Native AOT-safe C#
backend and produces an `.exe` on Windows or a native executable on Linux and
macOS.

```vela
include vela.core.console as console;

class Greeter(prefix: Text = "Hello") {
    fn greet(name: Text) -> Void {
        console.write_line(prefix + ", " + name + "!");
    }
}

fn main() -> Void {
    Greeter().greet(name: "Vela");
}
```

## Why Vela

- Native AOT applications with a compact, trimmable runtime.
- Checked overflow, division, indexes, null access, casts, and boxing/unboxing.
- O(1) expected lookup in `HashMap` and `HashSet`; O(1) indexing in vectors and
  arrays.
- Classes with mandatory primary constructors, structs, records, generics, and
  any number of validated interface contracts.
- Exhaustive `match` for enums, `Option<T>`, and `Result<T,E>`.
- Explicit opt-in modules and source-linked standard libraries with no runtime
  reflection requirement.
- Verbose, color-aware compiler phases by default and `--quiet` when desired.
- Signed installer design for Windows, macOS, and Linux with a global `vela`
  command.

## Start here

1. [Install and compile your first program](articles/getting-started.md).
2. Learn the [language and type system](articles/language-tour.md).
3. Use [core runtime modules](articles/core-modules.md).
4. Build tools with [CLI and logging packages](articles/standard-library.md).
5. Understand [diagnostics and safety](articles/diagnostics.md).

> [!NOTE]
> Vela is still experimental. The docs distinguish implemented behavior from
> planned functionality; unsupported features are not presented as complete.
