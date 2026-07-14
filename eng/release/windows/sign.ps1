[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Paths,

    [string]$CertificateBase64 = $env:WINDOWS_CERTIFICATE_PFX_BASE64,
    [string]$CertificatePassword = $env:WINDOWS_CERTIFICATE_PASSWORD,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CertificateBase64) -or [string]::IsNullOrWhiteSpace($CertificatePassword)) {
    throw 'WINDOWS_CERTIFICATE_PFX_BASE64 and WINDOWS_CERTIFICATE_PASSWORD are required for a signed release.'
}

$certificatePath = Join-Path $env:RUNNER_TEMP 'vela-release-signing.pfx'
try {
    [System.IO.File]::WriteAllBytes($certificatePath, [System.Convert]::FromBase64String($CertificateBase64))

    foreach ($path in $Paths) {
        $resolvedPath = (Resolve-Path -LiteralPath $path).Path
        & signtool sign '/fd' 'SHA256' '/f' $certificatePath '/p' $CertificatePassword '/tr' $TimestampUrl '/td' 'SHA256' $resolvedPath
        if ($LASTEXITCODE -ne 0) { throw "signtool failed to sign '$resolvedPath' with exit code $LASTEXITCODE." }

        & signtool verify '/pa' '/all' $resolvedPath
        if ($LASTEXITCODE -ne 0) { throw "signtool could not verify '$resolvedPath' with exit code $LASTEXITCODE." }
    }
}
finally {
    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue
}
