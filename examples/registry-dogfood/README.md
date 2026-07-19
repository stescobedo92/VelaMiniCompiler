# Registry dogfood

This example shows how to publish a tiny library package into a local file registry with signed TUF metadata.

## Package layout

- `package/` — source package with `vela.toml` and `src/lib.vela`
- `registry-data/` — populated by the pack script

## Pack a file registry

From the repository root:

```powershell
./examples/registry-dogfood/pack-registry.ps1
```

The script:

1. packs `package/` into `registry-data/dogfood.lib/0.1.0/package.vlpkg`
2. writes signed `registry-data/tuf/root.json` and `registry-data/tuf/targets.json`
3. uses an ephemeral ECDSA P-256 test key (development only)

## Restore

```powershell
dotnet test tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj --filter TufRestore
```

Or from Vela once wired through CLI:

```text
vela restore dogfood.lib --registry file:///absolute/path/to/examples/registry-dogfood/registry-data
```

## HTTP registry

Start the registry server with the dogfood data root:

```powershell
$env:RegistryRoot = (Resolve-Path examples/registry-dogfood/registry-data)
dotnet run --project src/Vela.Registry.Server
```

TUF metadata is served from `/tuf/root.json` and `/tuf/targets.json`.
