# Vela productivity, systems, language, documentation, and pipeline design

## Status

Conversational design approved. This written specification is awaiting final
user review; implementation has not started.

This specification consolidates the user-approved expansion of Vela for both
server applications and command-line tools. It covers language changes, core
runtime modules, source-linked standard-library packages, documentation, tests,
and release pipeline corrections.

## Goals

1. Add a safe, bounded process API and a broader cross-platform text/file API.
2. Expand the existing `vela.std.log` and `vela.std.cli` packages rather than
   duplicating them.
3. Add four language capabilities: exhaustive pattern matching, named/default
   arguments, destructuring, and declaration attributes.
4. Add user-facing `Void`, mandatory class primary constructors in the class
   header, and multiple interface implementation.
5. Emit actionable compiler errors whenever syntax, type, constructor,
   interface, match, attribute, process, or I/O rules are violated.
6. Publish a searchable documentation site with the same practical depth as the
   swaggercpp reference: architecture, installation, progressive examples,
   API/module reference, packaging, diagnostics, and releases.
7. Make unsigned packaging testable in CI while keeping tag releases strictly
   signed.
8. Preserve Native AOT compatibility, deterministic output, checked failures,
   source locations, and the small-runtime objective.

## Non-goals

- No implicit shell invocation or command-string interpolation.
- No background daemon/process supervisor in this phase.
- No binary byte-array file API until Vela has a dedicated byte type.
- No secondary or overloaded constructors in this phase.
- No interface inheritance in this phase.
- No runtime reflection for attributes, CLI definitions, logging, or docs.
- No duplicate JSON implementation; all structured logging uses
  `vela.core.json`.
- No unsigned GitHub release from a version tag.
- No remote package registry or LSP/editor implementation in this phase.

## Delivery streams and order

The work is intentionally split into dependency-ordered streams:

1. language syntax and diagnostics;
2. backend/type-system semantics and Native AOT emission;
3. core runtime modules;
4. source-linked standard-library packages;
5. examples, exhaustive tests, and performance checks;
6. documentation site and documentation pipeline;
7. package-smoke and signed-release pipelines.

Each stream is independently testable. A later implementation plan may split
them into separate commits, but all behavior in this specification belongs to
one coherent release.

## Layering

| Layer | Ownership | Inclusion | New/expanded surface |
| --- | --- | --- | --- |
| Language | lexer/parser/binder/emitter | always | `Void`, primary constructors, multiple interfaces, `match`, named/default arguments, destructuring, attributes |
| `vela.core.*` | runtime/compiler | explicit import and Native AOT trimming | `system`, expanded `io`, minimal `console`, JSON quoting helper |
| `vela.std.*` | source-linked Vela packages | package dependency and import | expanded `log` and `cli` |
| Documentation | DocFX/static assets | build-time only | guides, reference, examples, diagnostics, releases |
| CI/CD | GitHub Actions | repository workflows | tests, package smoke, signed release, Pages deployment |

Heavy functionality stays outside the mandatory runtime. A program that imports
neither process nor standard-library packages must not retain those
implementations after Native AOT trimming.

# Language design

## Void

`Void` is the canonical source-language return type for functions and methods
that produce no value.

```vela
fn write_banner(message: Text) -> Void {
    print(message);
}

fn stop() -> Void {
    return;
}
```

Rules:

- `return;` is valid only in a `Void` function or method.
- Reaching the end of a `Void` body is valid.
- `return expression;` is invalid in `Void` code.
- A non-`Void` function must still return a compatible value on every reachable
  path.
- `Void` cannot be used for fields, local variables, parameters, collection
  elements, generic arguments, boxing, or FFI values.
- `main() -> Void` is valid and maps to process exit code zero.
- `main() -> Int` remains valid and returns the explicit process exit code.
- `Unit` remains a compatibility alias for one release, emits a deprecation
  warning in user source, and is documented as internal/legacy. Generated
  runtime code may continue to use the .NET unit representation.

Required diagnostics include:

| Condition | Severity | Required behavior |
| --- | --- | --- |
| `return value;` in `Void` | error | point to the returned expression and suggest `return;` |
| `return;` in non-`Void` | error | require a value of the declared type |
| non-`Void` missing a return path | error | identify the declaration and missing path |
| `Void` used as a value type | error | explain that `Void` is return-only |
| legacy `Unit` in user source | warning | suggest `Void` without breaking existing packages |
| invalid `main` return type | error | allow only `Int` or `Void` |

## Mandatory primary constructors

Every class declaration includes exactly one primary constructor parameter list
immediately after the class name.

```vela
class Empty() {
}

class Counter(start: Int, label: Text = "counter") implements Printable {
    var value: Int = start;

    fn text() -> Text {
        return label + ": " + Text(self.value);
    }
}
```

Rules:

- `class A { }` is invalid; the parser requires `class A() { }`.
- Constructor parameters are typed, immutable, and scoped to instance field
  initializers and instance methods.
- Parameters do not automatically become public fields or properties.
- A field may explicitly capture a parameter, as in `var value: Int = start;`.
- Default values follow the same constant/default-expression restrictions as
  function defaults.
- Creation uses `A(arguments)`. There is no implicit parameterless constructor
  when the header declares parameters.
- Secondary constructors and constructor overloading are deferred.
- Struct construction remains field-based in this phase; the mandatory
  constructor rule applies to `class` only.
- Existing examples and tests must be migrated to explicit parentheses.

Required errors:

- missing class constructor parentheses;
- duplicate constructor parameter;
- missing parameter type;
- default value type mismatch;
- required argument omitted;
- too many arguments;
- unknown, repeated, or out-of-order named argument;
- constructor argument type mismatch;
- reference to an unknown constructor parameter;
- field left without a legal initializer when definite initialization requires
  one.

## Multiple interfaces

A class implements zero, one, or many interfaces:

```vela
class Worker(name: Text) implements Runnable, Printable, Resettable {
    fn run() -> Void {
        print(name);
    }

    fn text() -> Text {
        return name;
    }

    fn reset() -> Void {
    }
}
```

Rules:

- Interface names are comma-separated after one `implements` keyword.
- Duplicate interface names are errors.
- Every interface must resolve to a declared interface.
- The class must implement every required method with matching name, generic
  arity, parameters, return type, and async modifier.
- One class method may satisfy identical signatures from several interfaces.
- Conflicting interface signatures with the same method name are errors unless
  one class method can satisfy all contracts exactly.
- Interfaces do not inherit or implement other interfaces in this phase.
- Emitted C# lists every interface and remains compatible with Native AOT.

Diagnostics name both the class and originating interface and point to the
nearest relevant source span. Missing contracts list each required signature,
not merely the first missing method.

## Named and default arguments

Functions, methods, record construction, and class primary constructors support
defaults and named calls.

```vela
fn connect(host: Text, port: Int = 8080, timeout_ms: Int = 5000) -> Void {
}

connect("localhost");
connect("localhost", timeout_ms: 1000);
connect(host: "localhost", port: 9000);
```

Rules:

- Required parameters precede optional parameters in declarations.
- Default expressions are evaluated at the call site from left to right.
- Defaults may reference earlier parameters but not later parameters.
- Positional arguments must precede named arguments.
- Named arguments may appear in any order after positional arguments.
- Each parameter may be supplied at most once.
- Generic inference considers both positional and named arguments.
- Public FFI functions cannot expose optional parameters; the ABI remains
  explicit.

Errors cover duplicate/unknown names, missing required values, positional
arguments after named arguments, invalid defaults, cycles, type mismatches, and
ambiguous overload-like calls.

## Exhaustive match

`match` is a statement in the first phase. It supports enums, `Option<T>`,
`Result<T, E>`, and literal primitive cases.

```vela
match result {
    case Ok(value) {
        print(value);
    }
    case Err(error) {
        log.error(error);
    }
}

match option {
    case Some(value) { print(value); }
    case None { print("missing"); }
}
```

Rules:

- Enum, `Option`, and `Result` matches are exhaustive unless `default` exists.
- Patterns may bind one contained value where the variant carries one.
- Bindings are immutable and scoped to the case block.
- Duplicate or unreachable cases are errors.
- A wildcard/default case must be last.
- Literal matching uses the subject's exact type; no implicit numeric mixing.
- Nested, guarded, list, and object patterns are deferred.
- Exhaustive matches participate in definite-return analysis.

Diagnostics identify missing variants, duplicate patterns, incompatible
patterns, invalid binding counts, unreachable cases, and non-exhaustive
control-flow.

## Destructuring

The first phase supports immutable one-level destructuring for tuples and
records.

```vela
let (host, port) = ("localhost", 8080);
let ServerConfig { name, timeout_ms } = config;
```

Rules:

- Tuple expressions and tuple types have fixed arity of two through eight.
- Tuple destructuring requires the exact arity.
- Record destructuring names existing public fields exactly once.
- `_` discards one value.
- Destructured bindings are immutable `let` bindings.
- Nested patterns, rest patterns, and mutable destructuring are deferred.
- Duplicate bindings conflict with the current lexical scope.

Errors cover non-destructurable values, tuple arity mismatch, unknown/duplicate
record fields, missing required record fields when exact destructuring is used,
and duplicate local bindings.

## Declaration attributes

Attributes are compile-time metadata placed directly before a declaration.

```vela
@since("0.2.0")
@experimental
class CommandApp(name: Text) {
    @deprecated("Use run instead.")
    fn execute() -> Void {
    }
}
```

Initial built-ins:

| Attribute | Targets | Effect |
| --- | --- | --- |
| `@deprecated("message")` | public declarations and members | warning at each use; rendered in docs |
| `@experimental` | declarations and members | warning at each external use; rendered badge |
| `@since("version")` | public declarations and members | validated metadata for docs |
| `@doc(hidden)` | public declarations and members | omitted from generated public reference |

Rules:

- Attributes are preserved with exact spans for future IDE/LSP consumers.
- Unknown attributes, duplicate singleton attributes, wrong argument counts,
  invalid argument types, and invalid targets are compiler errors.
- Attributes do not use runtime reflection and do not automatically retain code
  under Native AOT.
- Documentation comments and attributes are both accepted; contradictions such
  as `@deprecated` with incompatible documentation metadata produce a warning.

# Core runtime modules

## vela.core.system

`vela.core.system` executes child processes without invoking a shell.

Proposed API:

```vela
include vela.core.system as system;

let result = system.exec(
    "git",
    ["--version"],
    timeout_ms: 5000,
    max_output_bytes: 1048576
);

if result.exit_code != 0 {
    print(result.stderr);
}
```

Public values and functions:

| API | Result |
| --- | --- |
| `exec(program, args, timeout_ms = 30000, max_output_bytes = 1048576)` | `ProcessResult` |
| `which(program)` | `Option<Text>` |
| `process_id()` | `Int` |
| `temporary_directory()` | `Text` |

`ProcessResult` contains `exit_code: Int`, `stdout: Text`, `stderr: Text`,
`timed_out: Bool`, and `truncated: Bool`.

Security and performance rules:

- `UseShellExecute=false` and an argument list API are mandatory.
- The module never concatenates arguments into a shell command.
- stdout and stderr are drained concurrently to prevent pipe deadlocks.
- timeout is checked and the process tree is terminated where supported.
- captured output is bounded; exceeding the limit marks `truncated` and stops
  unbounded growth.
- invalid timeout/limit values fail before starting a process.
- missing executables, denied access, invalid encodings, start failures, and
  termination failures become source-aware `VelaProcessException` errors.
- `which` follows host PATH semantics without executing the program.
- no process handle escapes through the native package ABI.

## Expanded vela.core.io

Existing `exists`, `read_text`, `write_text`, and `append_text` remain.

Added API groups:

| Group | Operations |
| --- | --- |
| Files | `delete_file`, `copy_file`, `move_file`, `file_size`, `read_lines`, `write_lines` |
| Directories | `directory_exists`, `create_directory`, `delete_directory`, `list_files`, `list_directories` |
| Paths | `combine`, `file_name`, `extension`, `full_path` |
| Temporary | `temporary_file`, `temporary_directory` |

Rules:

- text is UTF-8 without a BOM unless explicitly preserved by a future API;
- list operations return ordinally sorted paths for deterministic builds;
- recursive deletion requires an explicit Boolean argument and rejects an empty
  or root path;
- copy/move overwrite behavior is explicit;
- line APIs use `Array<Text>` and preserve line contents without terminators;
- platform I/O errors become `VelaIoException` with source locations;
- invalid paths, bounds, negative sizes, and unsafe recursive targets fail
  before mutation;
- no implicit current-directory changes occur.

## vela.core.console

A minimal console primitive supports the source-linked CLI and logger:

| API | Purpose |
| --- | --- |
| `write` / `write_line` | stdout |
| `write_error` / `write_error_line` | stderr |
| `is_output_redirected` | disable terminal decoration |
| `supports_color` | decide whether ANSI color is appropriate |

It performs no formatting policy. Color, levels, help layout, and JSON events
remain in `vela.std`.

## vela.core.json addition

`json.quote(text) -> Text` returns a valid JSON string literal using the existing
`System.Text.Json`-based implementation. `vela.std.log` uses it to build JSON
events without duplicating escaping logic.

# Source-linked standard library

## Expanded vela.std.log

The package stays source-linked and imports `vela.core.time`,
`vela.core.console`, and `vela.core.json`.

```vela
include vela.std.log as log;

let logger = log.Logger(
    "worker",
    min_level: log.Level.Info,
    json: false
);

logger.info("started");
logger.error("request failed");
```

Surface:

- `Level` enum: `Trace`, `Debug`, `Info`, `Warn`, `Error`;
- `Logger(name, min_level = Info, json = false, color = true)`;
- methods `trace`, `debug`, `info`, `warn`, `error`;
- immutable context via `with_field(key, value)`;
- compatibility helpers `log.info`, `log.warn`, `log.json`, and `log.timed`.

Behavior:

- output below `min_level` is skipped before allocating a formatted message;
- errors use stderr; other levels use stdout;
- color is disabled when redirected or not supported;
- JSON mode emits one object per line and delegates all quoting/canonicalization
  to `vela.core.json`;
- timestamps are UTC Unix milliseconds;
- field order is deterministic;
- invalid level/configuration and malformed JSON payloads produce explicit
  source-aware errors.

## Expanded vela.std.cli

The package stays source-linked and imports `vela.core.env`,
`vela.core.console`, and `vela.core.text`.

```vela
include vela.std.cli as cli;

fn main() -> Int {
    let app = cli.Command(
        "serve",
        "Starts the HTTP service"
    )
        .option("--host", "Host name", default_value: "127.0.0.1")
        .option("--port", "TCP port", required: true)
        .flag("--verbose", "Enable verbose logs");

    let parsed = app.parse();
    if parsed.is_error {
        cli.print_error_and_help(app, parsed.error);
        return 2;
    }

    let port = parsed.value.require_int("--port");
    print(port);
    return 0;
}
```

Surface:

- `Command(name, description)`;
- subcommands, options, flags, aliases, required values, defaults, and help;
- typed getters for `Text`, `Int`, `Long`, `Double`, and `Bool`;
- `parse(args)` plus `parse_environment()`/`parse()`;
- deterministic `render_help` and `render_version`;
- compatibility helpers `argument_or`, `has_flag`, and `current_directory`.

Rules:

- duplicate option names/aliases and invalid definitions fail when constructing
  the command;
- unknown options, missing values, type conversion failures, missing required
  options, and unexpected positionals return structured `CliError` values;
- `--help` and `--version` do not terminate the process inside the library;
- the application decides the exit code;
- help ordering follows definition order and wraps predictably;
- parsing is linear in argument count plus declared options, with an indexed
  lookup for option names.

# Diagnostics contract

Every invalid source-language rule, plus every library misuse that can be
decided statically, must produce a structured compiler diagnostic rather than
a generated C# error or an unhandled compiler exception. Dynamic failures such
as a missing executable, denied file access, a timeout, or an operating-system
error instead produce the documented Vela exception or result value; host
implementation exceptions must not leak through the language boundary.

The implementation reserves `P012` onward for new parse errors and `VEL3020`
onward for new semantic errors. Exact assignments are fixed in the
implementation plan and covered by tests.

Each diagnostic must provide:

- stable code and severity;
- exact source span;
- concise message naming the violated rule;
- actionable help text;
- related declaration/interface name where applicable;
- deterministic ordering when several errors occur.

Error recovery must allow the parser/binder to report multiple independent
problems in one compilation without looping or corrupting later spans.

Representative compiler-negative cases are required for every static rule in
the Void, constructor, multiple-interface, match, argument, destructuring,
attribute, system, I/O, log, and CLI sections. Runtime-only failure rules need
separate execution tests that assert their Vela exception type, message, and
non-destructive behavior.

# Documentation site

## Technology and structure

Use DocFX to build a static, searchable site deployable to GitHub Pages. The
site is documentation infrastructure only and is not linked into Vela programs.

Proposed information architecture:

- Home: value proposition, supported platforms, current stability.
- Get started: installation, hello world, check/run/build.
- Language guide: syntax, types, safety, control flow, async, classes, Void,
  constructors, interfaces, match, destructuring, attributes.
- Core reference: one page per `vela.core.*` module with signatures,
  complexity, platform behavior, exceptions, and examples.
- Standard library: one page per `vela.std.*` package.
- Compiler CLI: all commands, flags, colors, verbosity, targets, exit codes.
- Packages and native ABI: manifests, lockfiles, source libraries, native
  libraries.
- Examples: progressive runnable applications from simple to server/CLI tools.
- Diagnostics: searchable code catalog with cause, example, and resolution.
- Performance and security: complexity, Native AOT size, process/I/O limits.
- Installation and releases: signed artifacts, checksums, attestations.
- Contributor guide: architecture, tests, docs build, release process.
- Changelog/versioning.

The site takes inspiration from the swaggercpp documentation's practical
coverage of architecture, usage, consumption, examples, packaging, and
releases, while using original Vela content and examples.

## Documentation quality gates

- Every public module function, class, enum, method, field, and compiler command
  has a reference entry.
- Every new feature has at least one valid and two invalid examples.
- Code displayed in guides comes from checked-in `examples` files or mirrored
  test fixtures.
- CI runs `vela check` on Vela examples used by documentation.
- Internal links and DocFX build warnings fail the docs job.
- Syntax highlighting includes a Vela grammar for braces, comments, keywords,
  types, strings, numbers, and attributes.
- GitHub Pages deployment occurs only from the default branch.
- Pull requests build the site as an artifact without deploying it.

# Pipeline corrections

## Workflow separation

1. `ci.yml`: restore, build, test, format/static checks, and Vela example checks
   on Windows, Linux, and macOS.
2. `package-smoke.yml`: build unsigned Native AOT binaries and installers on all
   target platforms without signing secrets; upload short-lived workflow
   artifacts.
3. `release.yml`: version tags only; validate all signing secrets before
   expensive builds, then sign, notarize, attest, checksum, and publish.
4. `docs.yml`: build DocFX on pull requests and deploy GitHub Pages from the
   default branch.

## Release behavior

- A version tag can never publish unsigned artifacts.
- Missing Windows, Apple, or GPG credentials fail in a dedicated preflight job
  with the exact missing secret names.
- Package-smoke proves MSI/EXE, PKG/DMG, DEB/RPM construction independently of
  production certificates.
- The Linux interpolation regression is covered by a shell test asserting
  `vela_0.1.0_amd64.deb`.
- Action dependencies move to current Node 24-compatible releases and are
  pinned by commit SHA where practical.
- Workflows use least-privilege permissions per job.
- Release jobs use the protected `release` environment.
- Docs deployment uses the protected GitHub Pages environment.
- macOS PKG is signed/notarized before it is embedded into the DMG.
- Windows signs the compiler before MSI creation, then signs MSI and bundle.
- Linux publishes detached GPG signatures and the public key.
- Checksums include every final release asset.
- Release publication waits for every architecture and signing verification.

# Examples

At minimum, add:

- `void-functions.vela`;
- `primary-constructors.vela`;
- `multiple-interfaces.vela`;
- `match-option-result.vela`;
- `named-default-arguments.vela`;
- `destructuring.vela`;
- `attributes.vela`;
- `system-exec.vela`;
- `io-workspace.vela`;
- a source-linked `vela-cli-app` package;
- a source-linked `vela-logging-app` package;
- a combined server-tool example using CLI, logs, process execution, and files.

Destructive/process examples must operate only in a temporary directory and run
trusted local executables with bounded output.

# Testing strategy

## Parser and syntax

- valid/invalid `Void` declarations and returns;
- mandatory class parentheses and primary parameters;
- one/many/duplicate interfaces;
- named/default call grammar;
- match patterns and recovery;
- tuple/record destructuring;
- attributes and target validation.

## Semantic and backend

- return-path analysis for `Void` and non-`Void`;
- constructor scope, initialization, defaults, calls, and C# emission;
- merged interface contract verification and conflicts;
- exhaustive pattern type checking and definite return;
- named argument resolution and generic inference;
- destructuring types and binding scopes;
- attribute metadata and warnings;
- generated C# compiles with warnings as errors.

## Runtime and modules

- process stdout/stderr, nonzero exit, timeout, truncation, missing command, and
  argument-injection resistance;
- I/O success/failure, deterministic ordering, root deletion rejection, and
  cross-platform path behavior;
- logging levels, redirection, JSON escaping, deterministic fields, and stderr;
- CLI parsing, subcommands, aliases, defaults, conversions, help, and all
  errors;
- Native AOT publish tests for representative imports.

## Pipeline and docs

- actionlint and YAML parsing;
- Bash, PowerShell, and installer source syntax;
- package-smoke artifact-name assertions;
- missing-secret preflight tests;
- DocFX warnings-as-errors and link validation;
- checked documentation examples;
- signed-release verification steps remain conditional only on the signed
  release workflow, never on package smoke.

## Performance gates

- `system.exec` capture never exceeds configured bounds;
- CLI lookup is expected O(1) after definition indexing;
- disabled log levels avoid message-format allocation where possible;
- directory listings are O(n log n) only because deterministic sorting is
  explicit;
- core operations avoid reflection and dynamic code;
- baseline hello-world Native AOT size remains within the existing project
  budget;
- opt-in module size deltas are measured and documented.

# Acceptance criteria

The work is complete only when:

1. all approved syntax parses and emits Native AOT-compatible code;
2. every invalid rule has an asserted diagnostic code, span, message, and help;
3. existing examples are migrated and remain valid;
4. all core/std APIs work on Windows, Linux, and macOS where supported;
5. process and I/O safety limits are enforced;
6. logging reuses `vela.core.json`;
7. the CLI library creates real command trees and deterministic help;
8. tests cover positive, negative, boundary, and cross-platform cases;
9. the documentation site builds cleanly and is publishable to GitHub Pages;
10. package smoke succeeds without certificates;
11. version-tag releases cannot publish without valid signatures;
12. README and language/module reference accurately list the final surface.
