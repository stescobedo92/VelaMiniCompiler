[CmdletBinding()]
param(
    [string]$Version = '',
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repositoryRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
}

$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must use X.Y.Z format."
}

$fileVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
if ($Version -ne $fileVersion) {
    throw "Refusing to tag v$Version because VERSION file is '$fileVersion'."
}

$tag = "v$Version"
$existing = git tag -l $tag
if ($existing) {
    throw "Tag '$tag' already exists."
}

git tag -a $tag -m "Vela $Version"
Write-Output "Created annotated tag $tag"
if ($Push) {
    git push origin $tag
    Write-Output "Pushed $tag to origin (Release compiler workflow should start)."
} else {
    Write-Output "Run with -Push to publish the tag, or: git push origin $tag"
}
