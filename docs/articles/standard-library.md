---
title: Standard library packages
---

# Standard library packages

Packages under `packages/` are written in Vela and linked from source. Their
public names are reached through the dependency alias, while implementation
symbols remain package-qualified during compilation.

## `vela.std.log`

`log.logger(name, min_level: 2, json: false, color: true)` creates a logger with
Trace (0), Debug (1), Info (2), Warn (3), and Error (4) filtering. `with_field`
returns a logger with deterministic structured context. Human output is colored
only when the terminal supports it; JSON strings are escaped by
`vela.core.json` and errors use stderr.

```vela
include vela.std.log as log;

let logger = log.logger("worker", min_level: 1);
logger.debug("connected");
logger.with_field("job", "42").info("completed");
```

## `vela.std.cli`

`cli.command` builds deterministic command definitions. Options and subcommands
use `HashMap` indexes for expected O(1) lookup. The parser supports long names,
aliases, flags, required/default values, subcommands, help/version, and typed
`Text`, `Int`, `Long`, `Double`, and `Bool` accessors.

```vela
include vela.std.cli as cli;

let command = cli.command("serve", "Start a service", "0.2.0");
command.option("--port", "Listening port", default_value: "8080", alias: "-p");
command.flag("--verbose", "Enable verbose output", alias: "-v");
let result = command.parse();
let port = result.require_int("--port");
```

Invalid definitions fail early through Vela assertions. User input errors are
returned as `VelaCliResult`, allowing the application to choose its exit code.

## Other packages

- `vela.std.config`: environment fallbacks and JSON configuration access.
- `vela.std.http`: bounded plaintext HTTP/1.1 over async core TCP. It does not
  claim TLS, redirects, streaming, or a general HTTP client surface.
- `vela.std.test`: small assertion/timing helpers for Vela examples.

Both config and HTTP delegate JSON operations to `vela.core.json`; there is no
duplicated JSON parser.
