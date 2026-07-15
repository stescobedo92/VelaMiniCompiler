---
title: Core modules
---

# Core modules

Core modules are explicit so unused functionality can be trimmed from Native
AOT output. Use `include vela.core;` for the base language surface or import a
module with an alias.

## `vela.core.system`

Executes a child process directly with `UseShellExecute=false`; no shell command
line is constructed. Standard output and error are drained concurrently, UTF-8
capture is bounded, timeouts kill the process tree, and failures use
`VelaProcessException`.

```vela
include vela.core.system as system;

let result = system.exec(
    "dotnet",
    ["--version"],
    timeout_ms: 5000,
    max_output_bytes: 65536
);
print(result.exit_code);
print(result.stdout);
```

API: `exec`, `which`, `process_id`, and `temporary_directory`. `ProcessResult`
exposes `exit_code`, `stdout`, `stderr`, `timed_out`, and `truncated`.

## `vela.core.io`

File operations are source-aware and deterministic. Directory listings use
ordinal sorting; recursive deletion rejects filesystem roots.

- Read/write: `read_text`, `write_text`, `append_text`, `read_lines`,
  `write_lines`.
- Files: `exists`, `delete_file`, `copy_file`, `move_file`, `file_size`.
- Directories: `directory_exists`, `create_directory`, `delete_directory`,
  `list_files`, `list_directories`.
- Paths: `combine`, `file_name`, `extension`, `full_path`.
- Isolation: `temporary_file`, `temporary_directory`.

## `vela.core.console`

`write`, `write_line`, `write_error`, and `write_error_line` keep stdout and
stderr explicit. `is_output_redirected` and `supports_color` respect redirection,
`NO_COLOR`, and `TERM=dumb`.

## Data, text, and security

- `vela.core.json`: validation, compacting, escaping/quoting, and typed
  top-level property access. Standard packages reuse this implementation.
- `vela.core.text`: search, casing, trimming, invariant formatting and parsing,
  and Unicode scalar creation with `from_code_point`.
- `vela.core.crypto`: SHA-256, HMAC-SHA256, secure equality, and random bytes.
- `vela.core.encoding`: UTF-8, hexadecimal, and Base64 conversion.
- `vela.core.math`, `time`, and `random`: checked helpers and clocks.
- `vela.core.env`: process arguments, environment, and current directory.

## Network and concurrency

- `vela.core.tcp` provides bounded synchronous and asynchronous TCP operations.
- `vela.concurrent` provides explicit cancellation handles.

TCP examples should target trusted local services. `system.exec` examples use
argument lists and bounded output; never join untrusted values into a shell
command.
