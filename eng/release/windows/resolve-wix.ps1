function Resolve-WixToolsetBin {
    [CmdletBinding()]
    param(
        [string]$RequestedPath,

        [Parameter(Mandatory)]
        [string[]]$RequiredTools
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add([System.IO.Path]::GetFullPath($RequestedPath))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:WIX)) {
        $candidates.Add((Join-Path $env:WIX 'bin'))
    }

    foreach ($tool in $RequiredTools) {
        $command = Get-Command $tool -ErrorAction SilentlyContinue
        if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
            $candidates.Add((Split-Path -Parent $command.Source))
        }
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        Get-ChildItem -LiteralPath $programFilesX86 -Directory -Filter 'WiX Toolset v3.*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { $candidates.Add((Join-Path $_.FullName 'bin')) }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $hasEveryTool = $true
        foreach ($tool in $RequiredTools) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidate $tool) -PathType Leaf)) {
                $hasEveryTool = $false
                break
            }
        }

        if ($hasEveryTool) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $tools = $RequiredTools -join ', '
    throw "WiX Toolset 3.x was not found. Required tools: $tools."
}
