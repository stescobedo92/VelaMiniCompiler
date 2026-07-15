[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$WixBin = "${env:ProgramFiles(x86)}\WiX Toolset v3.11\bin"
)

$ErrorActionPreference = 'Stop'

$candle = Join-Path $WixBin 'candle.exe'
$light = Join-Path $WixBin 'light.exe'
$heat = Join-Path $WixBin 'heat.exe'
foreach ($tool in @($candle, $light, $heat)) {
    if (-not (Test-Path -LiteralPath $tool)) {
        throw "WiX Toolset 3.11 was not found at '$WixBin'."
    }
}

$sourceDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$scriptDirectory = Split-Path -Parent $PSCommandPath
$wixSource = Join-Path $scriptDirectory 'vela.wxs'
$wixObject = Join-Path $outputPath 'vela.wixobj'
$runtimeWixSource = Join-Path $outputPath 'vela-runtime.wxs'
$runtimeWixObject = Join-Path $outputPath 'vela-runtime.wixobj'
$msiPath = Join-Path $outputPath "vela-$Version-win-x64.msi"
$runtimeDirectory = Join-Path $sourceDirectory 'runtime'
if (-not (Test-Path -LiteralPath (Join-Path $runtimeDirectory 'Vela.Runtime.csproj') -PathType Leaf)) {
    throw "The staged runtime directory '$runtimeDirectory' is incomplete."
}

& $heat 'dir' $runtimeDirectory '-nologo' '-cg' 'VelaRuntimeComponents' '-dr' 'RUNTIMEFOLDER' '-srd' '-ag' '-sfrag' '-var' 'var.RuntimeDir' '-out' $runtimeWixSource
if ($LASTEXITCODE -ne 0) { throw "heat.exe failed with exit code $LASTEXITCODE." }

& $candle '-nologo' '-arch' 'x64' "-dVersion=$Version" "-dSourceDir=$sourceDirectory" '-out' $wixObject $wixSource
if ($LASTEXITCODE -ne 0) { throw "candle.exe failed for the product source with exit code $LASTEXITCODE." }
& $candle '-nologo' '-arch' 'x64' "-dRuntimeDir=$runtimeDirectory" '-out' $runtimeWixObject $runtimeWixSource
if ($LASTEXITCODE -ne 0) { throw "candle.exe failed with exit code $LASTEXITCODE." }

& $light '-nologo' '-ext' 'WixUtilExtension' '-out' $msiPath $wixObject $runtimeWixObject
if ($LASTEXITCODE -ne 0) { throw "light.exe failed with exit code $LASTEXITCODE." }

Remove-Item -LiteralPath $wixObject, $runtimeWixObject, $runtimeWixSource -Force
Write-Output $msiPath
