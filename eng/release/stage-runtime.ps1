[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$runtimeSource = Join-Path $repositoryRoot 'src\Vela.Runtime'
$uiRuntimeSource = Join-Path $repositoryRoot 'src\Vela.Ui.Runtime'
$httpRuntimeSource = Join-Path $repositoryRoot 'src\Vela.Http.Runtime'
$grpcRuntimeSource = Join-Path $repositoryRoot 'src\Vela.Grpc.Runtime'
$globalJson = Join-Path $repositoryRoot 'global.json'
$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$runtimeDestination = Join-Path $publishPath 'runtime'
$uiRuntimeDestination = Join-Path $publishPath 'ui-runtime'
$httpRuntimeDestination = Join-Path $publishPath 'http-runtime'
$grpcRuntimeDestination = Join-Path $publishPath 'grpc-runtime'

function Copy-AdapterProject {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$ProjectFileName,
        [string[]]$ExtraDirectories = @()
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Runtime source directory '$Source' was not found."
    }

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    Get-ChildItem -LiteralPath $Source -File |
        Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq $ProjectFileName } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force }

    foreach ($dirName in $ExtraDirectories) {
        $from = Join-Path $Source $dirName
        if (Test-Path -LiteralPath $from -PathType Container) {
            $to = Join-Path $Destination $dirName
            Copy-Item -LiteralPath $from -Destination $to -Recurse -Force
        }
    }

    $projectPath = Join-Path $Destination $ProjectFileName
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Staged project '$projectPath' was not created."
    }

    $projectText = Get-Content -LiteralPath $projectPath -Raw
    $projectText = $projectText -replace '\.\.\\Vela\.Runtime\\Vela\.Runtime\.csproj', '..\runtime\Vela.Runtime.csproj'
    Set-Content -LiteralPath $projectPath -Value $projectText -NoNewline
}

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

Copy-AdapterProject -Source $uiRuntimeSource -Destination $uiRuntimeDestination -ProjectFileName 'Vela.Ui.Runtime.csproj'
Copy-AdapterProject -Source $httpRuntimeSource -Destination $httpRuntimeDestination -ProjectFileName 'Vela.Http.Runtime.csproj'
Copy-AdapterProject -Source $grpcRuntimeSource -Destination $grpcRuntimeDestination -ProjectFileName 'Vela.Grpc.Runtime.csproj' -ExtraDirectories @('Protos')

Write-Output $runtimeDestination
