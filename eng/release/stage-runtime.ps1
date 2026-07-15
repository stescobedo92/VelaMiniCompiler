[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$runtimeSource = Join-Path $repositoryRoot 'src\Vela.Runtime'
$globalJson = Join-Path $repositoryRoot 'global.json'
$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$runtimeDestination = Join-Path $publishPath 'runtime'

if (-not (Test-Path -LiteralPath $runtimeSource -PathType Container)) {
    throw "Runtime source directory '$runtimeSource' was not found."
}
if (-not (Test-Path -LiteralPath $globalJson -PathType Leaf)) {
    throw "SDK pin '$globalJson' was not found."
}

[System.IO.Directory]::CreateDirectory($runtimeDestination) | Out-Null
Get-ChildItem -LiteralPath $publishPath -File |
    Where-Object { $_.Extension -eq '.pdb' -or $_.Extension -eq '.xml' } |
    Remove-Item -Force
Get-ChildItem -LiteralPath $runtimeSource -File |
    Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq 'Vela.Runtime.csproj' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeDestination -Force }
Copy-Item -LiteralPath $globalJson -Destination (Join-Path $publishPath 'global.json') -Force

$expectedProject = Join-Path $runtimeDestination 'Vela.Runtime.csproj'
if (-not (Test-Path -LiteralPath $expectedProject -PathType Leaf)) {
    throw "Staged runtime project '$expectedProject' was not created."
}

Write-Output $runtimeDestination
