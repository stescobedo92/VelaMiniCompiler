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
foreach ($tool in @($candle, $light)) {
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
$msiPath = Join-Path $outputPath "vela-$Version-win-x64.msi"

& $candle '-nologo' '-arch' 'x64' "-dVersion=$Version" "-dSourceDir=$sourceDirectory" '-out' $wixObject $wixSource
if ($LASTEXITCODE -ne 0) { throw "candle.exe failed with exit code $LASTEXITCODE." }

& $light '-nologo' '-ext' 'WixUtilExtension' '-out' $msiPath $wixObject
if ($LASTEXITCODE -ne 0) { throw "light.exe failed with exit code $LASTEXITCODE." }

Remove-Item -LiteralPath $wixObject -Force
Write-Output $msiPath
