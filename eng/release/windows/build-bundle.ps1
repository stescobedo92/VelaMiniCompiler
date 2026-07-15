[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$MsiPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$WixBin
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptDirectory 'resolve-wix.ps1')
$WixBin = Resolve-WixToolsetBin -RequestedPath $WixBin -RequiredTools @('candle.exe', 'light.exe')
$candle = Join-Path $WixBin 'candle.exe'
$light = Join-Path $WixBin 'light.exe'

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$wixSource = Join-Path $scriptDirectory 'vela-bundle.wxs'
$wixObject = Join-Path $outputPath 'vela-bundle.wixobj'
$bundlePath = Join-Path $outputPath "vela-$Version-win-x64-setup.exe"

& $candle '-nologo' '-ext' 'WixBalExtension' "-dVersion=$Version" "-dMsiPath=$resolvedMsiPath" '-out' $wixObject $wixSource
if ($LASTEXITCODE -ne 0) { throw "candle.exe failed with exit code $LASTEXITCODE." }

& $light '-nologo' '-ext' 'WixBalExtension' '-out' $bundlePath $wixObject
if ($LASTEXITCODE -ne 0) { throw "light.exe failed with exit code $LASTEXITCODE." }

Remove-Item -LiteralPath $wixObject -Force
Write-Output $bundlePath
