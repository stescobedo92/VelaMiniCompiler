# Changelog

## 0.3.0 — 2026-07-18

### Added
- Cross-platform Avalonia GUI (`vela.core.gui`): layouts, menus, file dialogs,
  lists/grids, typed callbacks, slider/textarea/numeric/radio/separator.
- REST hosting (`vela.core.http`) on Kestrel with GET/POST/PUT/DELETE handlers.
- Minimal GraphQL (`vela.core.graphql`) mounted on HTTP.
- Unary gRPC (`vela.core.grpc`) with method-name dispatch.
- Adapter runtimes: `Vela.Http.Runtime`, `Vela.Grpc.Runtime` (plus existing UI).
- Lambdas returning `Text` and `fn(value: Int) -> Void` for API/UI handlers.
- Mintlify documentation site under `docs/` (local preview via `mint dev`).

### Changed
- Release staging includes `http-runtime/` and `grpc-runtime/` beside `ui-runtime/`.
- CI checks API/GUI examples and adapter test projects.
- GitHub Actions release flow reads `VERSION` and ships docs validation with tags.

## 0.2.0

- Desktop GUI foundation and installer PATH improvements.

## 0.1.0

- Initial public compiler release.
