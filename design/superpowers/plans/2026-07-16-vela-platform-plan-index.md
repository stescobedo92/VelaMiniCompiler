# Vela application platform plan index

The approved application-platform release is intentionally split into six
implementation plans. They form one release, but each plan must leave the
repository buildable and independently testable.

## Execution order

1. [Callbacks, capabilities, and ABI v2](2026-07-16-callbacks-capabilities-and-abi-v2.md)
   establishes typed callbacks, optional dependency capabilities, safe wire
   values, handles, async operations, versioned manifests, and C headers.
2. [Remote package registry client](2026-07-16-remote-package-registry-client.md)
   adds manifests, SemVer resolution, lockfiles, deterministic archives, TUF,
   cache, credentials, remote protocol, and CLI commands.
3. [HTTP, TLS, and data](2026-07-16-http-tls-and-data.md) adds isolated Kestrel,
   SQLite, PostgreSQL, and migration adapters plus source-linked Vela packages.
4. [Self-hosted registry server](2026-07-16-self-hosted-registry-server.md)
   dogfoods Vela HTTP/PostgreSQL and implements the web-ready registry API.
5. [Uno UI and Hello form](2026-07-16-uno-ui-and-hello-form.md) adds the modular
   UI runtime/packages, platform builds, accessibility, and the exact approved
   form example.
6. [Integration, documentation, and release](2026-07-16-integration-documentation-and-release.md)
   proves cross-subsystem applications, performs security/performance review,
   expands pipelines, and publishes the next unused SemVer tag.

## Checkpoint rule

Do not begin a later plan while an earlier plan has failing committed tests or
an unresolved security/compatibility review finding. A platform-specific job
can run concurrently only after its shared compiler/runtime contracts are green.

## Completion rule

The user-requested release is complete only after all six plans are checked,
the approved design acceptance criteria pass, the release commit is pushed,
the next unused tag is published, and its GitHub release assets are verified.
