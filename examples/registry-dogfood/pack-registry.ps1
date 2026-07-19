param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
)

$ErrorActionPreference = "Stop"
$env:VELA_BUILD_DOGFOOD = "1"

dotnet test (Join-Path $RepoRoot "tests/Vela.Packages.Tests/Vela.Packages.Tests.csproj") `
    --configuration Release `
    --filter "FullyQualifiedName~BuildDogfoodRegistryExample"

Write-Host "Registry ready at $(Join-Path $RepoRoot 'examples/registry-dogfood/registry-data')"
