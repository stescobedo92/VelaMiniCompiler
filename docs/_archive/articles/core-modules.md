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

## `vela.core.gui`

Cross-platform desktop GUI (Avalonia). Importing this module links
`Vela.Ui.Runtime` into the generated project.

- `show_message(title, message)` opens a modal information dialog.
- `prompt(title, label, initial_value)` returns the confirmed text.
- `run_hello_form(...)` / `run_counter_app(...)` demo apps.
- Component API:
  - Forms: `create_form`, `create_form_layout(title, w, h, "column"|"row"|"grid")`
  - Controls: `add_label`, `add_button`, `add_textbox`, `add_textarea`,
    `add_checkbox`, `add_radio`, `add_progress`, `add_slider`, `add_numeric`,
    `add_combo`, `add_list`, `add_grid`, `add_separator`
  - Values: `get_value` / `set_value` (slider/numeric), `on_value_changed(fn(value: Int) -> Void)`
  - Callbacks: `on_click`, `on_text_changed`, `on_checked_changed`
  - Menus/files: `add_menu_item`, `open_file`, `save_file`
  - Loop: `run(form)` (preferred)

Set `VELA_UI_HEADLESS=1` to skip window creation (useful in CI).

## `vela.core.http` / `vela.core.graphql` / `vela.core.grpc`

Opt-in API adapters (Kestrel / gRPC). Importing them links `Vela.Http.Runtime`
and/or `Vela.Grpc.Runtime`.

REST (`vela.core.http`):

```vela
include vela.core.http as http;

fn main() -> Int {
    let server = http.create_server("127.0.0.1", 0);
    http.get(server, "/health", fn() -> Text { return "{\"ok\":true}"; });
    http.post(server, "/echo", fn(body: Text) -> Text { return body; });
    let port = http.start(server);
    let body = http.client_get("127.0.0.1", port, "/health");
    http.stop(server);
    return 0;
}
```

GraphQL (`vela.core.graphql`) mounts on an HTTP server:

```vela
include vela.core.http as http;
include vela.core.graphql as gql;

fn main() -> Int {
    let schema = gql.create_schema();
    gql.query(schema, "hello", fn() -> Text { return "\"world\""; });
    let server = http.create_server("127.0.0.1", 8080);
    gql.mount(server, "/graphql", schema);
    return http.run(server);
}
```

gRPC (`vela.core.grpc`) maps unary methods by name (`Service/Method`) over the
shared `vela.rpc.VelaRpc` protobuf service:

```vela
include vela.core.grpc as grpc;

fn main() -> Int {
    let server = grpc.create_server("127.0.0.1", 50051);
    grpc.map(server, "hello.Greeter/SayHello", fn(request: Text) -> Text {
        return "{\"message\":\"hi\"}";
    });
    return grpc.run(server);
}
```

Examples: `examples/api-rest.vela`, `examples/api-graphql.vela`,
`examples/api-grpc.vela`.

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
