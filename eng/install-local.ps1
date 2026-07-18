# Publishes a local Native AOT `vela` compiler and adds it to the current user's PATH.
# Usage (from repo root):
#   pwsh -File eng/install-local.ps1
#   pwsh -File eng/install-local.ps1 -Prefix "$env:LOCALAPPDATA\Vela"

[CmdletBinding()]
param(
    [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Vela'),
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repositoryRoot 'src\Vela.Cli\Vela.Cli.csproj'
$publishDirectory = Join-Path $Prefix 'bin'

if (-not (Test-Path -LiteralPath $cliProject -PathType Leaf)) {
    throw "Vela CLI project not found at '$cliProject'."
}

Write-Host "Publishing vela to $publishDirectory ($RuntimeIdentifier)..."
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

dotnet publish $cliProject `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    "-p:PublishAot=true" `
    "-p:InvariantGlobalization=true" `
    --output $publishDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$stageScript = Join-Path $repositoryRoot 'eng\release\stage-runtime.ps1'
& $stageScript -PublishDirectory $publishDirectory

$velaExe = Join-Path $publishDirectory 'vela.exe'
if (-not (Test-Path -LiteralPath $velaExe -PathType Leaf)) {
    $legacy = Join-Path $publishDirectory 'Vela.Cli.exe'
    if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        Move-Item -LiteralPath $legacy -Destination $velaExe -Force
    }
}

if (-not (Test-Path -LiteralPath $velaExe -PathType Leaf)) {
    throw "Published compiler was not found at '$velaExe'."
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathParts = @()
if (-not [string]::IsNullOrWhiteSpace($userPath)) {
    $pathParts = $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

if ($pathParts -notcontains $publishDirectory) {
    $updated = ($pathParts + $publishDirectory) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    Write-Host "Added to user PATH: $publishDirectory"
} else {
    Write-Host "User PATH already contains: $publishDirectory"
}

$env:Path = "$publishDirectory;$env:Path"
Write-Host ""
Write-Host "Installed: $velaExe"
Write-Host "Open a new terminal (or use this session) and run:"
Write-Host "  vela --help"
Write-Host "  vela run examples/ui-components.vela"
