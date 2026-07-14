# Releasing and installing Vela

The repository includes two GitHub Actions workflows:

- `Continuous Integration` builds and tests Windows, Linux, and macOS for every
  pull request and push to the primary branches.
- `Release compiler` produces Native AOT compiler binaries and installation
  artifacts when a version tag such as `v0.2.0` is pushed.

A tag-triggered release is always signed. It fails closed if a credential is
missing; it never silently publishes an unsigned production package. A manually
dispatched run can produce unsigned artifacts for packaging tests, but cannot
create a GitHub Release unless signing is enabled.

## Release artifacts

| Platform | Architectures | Artifacts | Global command |
| --- | --- | --- | --- |
| Windows | x64 | standalone `.exe`, `.msi`, graphical `-setup.exe` | machine `PATH` points to `C:\Program Files\Vela` |
| macOS | Apple Silicon, Intel | standalone executable, signed/notarized `.pkg` and `.dmg` | `/usr/local/bin/vela` symlink |
| Linux | x64 | standalone executable, `.deb`, `.rpm`, GPG signatures | `/usr/local/bin/vela` symlink |

The Windows setup executable is a WiX Burn graphical wizard. It installs the
MSI per-machine, requests elevation, supports repair/uninstall, and updates the
machine `PATH`. The MSI remains suitable for enterprise deployment tools.

The macOS installer stores the binary at `/usr/local/lib/vela/vela` and creates
`/usr/local/bin/vela`. DEB and RPM packages use that same layout. Every platform
job creates a GitHub provenance attestation for the standalone compiler. Release
assets include `SHA256SUMS`; Linux also publishes detached signatures and
`vela-linux-signing-key.asc`.

## Configure signing

Create a protected GitHub Environment named `release`. Restrict it to protected
version tags and require a reviewer before production releases. Store these
environment secrets there:

| Platform | Required secrets |
| --- | --- |
| Windows | `WINDOWS_CERTIFICATE_PFX_BASE64`, `WINDOWS_CERTIFICATE_PASSWORD` |
| macOS | `MACOS_APPLICATION_CERTIFICATE_P12_BASE64`, `MACOS_APPLICATION_CERTIFICATE_PASSWORD`, `MACOS_INSTALLER_CERTIFICATE_P12_BASE64`, `MACOS_INSTALLER_CERTIFICATE_PASSWORD`, `MACOS_APPLICATION_IDENTITY`, `MACOS_INSTALLER_IDENTITY`, `APPLE_NOTARY_KEY_ID`, `APPLE_NOTARY_ISSUER_ID`, `APPLE_NOTARY_KEY_P8_BASE64` |
| Linux | `LINUX_GPG_PRIVATE_KEY`, `LINUX_GPG_KEY_ID`, optionally `LINUX_GPG_PASSPHRASE` |

Use an organization-owned code-signing certificate for Windows. Base64 encode
the PFX before saving it. On macOS, use separate Developer ID Application and
Developer ID Installer certificates; base64 encode both P12 files and set the
identity values to the names reported by `security find-identity`. Base64 encode
the Notary API private key too.

The Linux private key must be ASCII-armored and `LINUX_GPG_KEY_ID` should be the
full fingerprint. It stays only in the protected environment; the workflow
exports its public key to the release. Signing secrets are never available to
pull-request workflows.

## Create a release

After configuring the credentials:

```powershell
git checkout main
git pull --ff-only
git tag v0.2.0
git push origin v0.2.0
```

The tag runs signing, packaging, provenance attestation, checksum generation,
and GitHub release creation. Tags must use exactly `vX.Y.Z`, because MSI
versions require numeric three-part versions.

For a dry run, use **Actions → Release compiler → Run workflow**, set
`sign=false` and `publish=false`. For a recovery release, use
`sign=true` and `publish=true` only when the corresponding signed Git tag already
exists.

## Install Vela

### Windows

Use `vela-<version>-win-x64-setup.exe` for the normal graphical wizard or the
MSI for managed deployment.

```powershell
.\vela-0.2.0-win-x64-setup.exe
# Close and reopen the terminal after installation.
vela --help
```

For unattended installation:

```powershell
msiexec /i .\vela-0.2.0-win-x64.msi /qn /norestart
```

The installer adds `C:\Program Files\Vela` to the machine `PATH`. Uninstall via
**Installed apps**.

### macOS

Download `macos-arm64` for Apple Silicon or `macos-x64` for Intel. Open the DMG
and run its PKG, or install directly:

```bash
sudo installer -pkg vela-0.2.0-macos-arm64.pkg -target /
vela --help
```

The installer creates the global `/usr/local/bin/vela` link. Uninstall with:

```bash
sudo rm -f /usr/local/bin/vela
sudo rm -rf /usr/local/lib/vela
```

### Debian and Ubuntu

```bash
sudo apt install ./vela_0.2.0_amd64.deb
vela --help
```

### Fedora, RHEL, and compatible systems

```bash
sudo dnf install ./vela-0.2.0-1.x86_64.rpm
vela --help
```

Both Linux packages install a global `vela` command. Remove it with
`sudo apt remove vela` or `sudo dnf remove vela`.

## Verify a download

Check hashes:

```bash
sha256sum --check SHA256SUMS
```

Verify Windows Authenticode:

```powershell
(Get-AuthenticodeSignature .\vela-0.2.0-win-x64-setup.exe).Status
```

The expected status is `Valid`. Verify macOS:

```bash
pkgutil --check-signature vela-0.2.0-macos-arm64.pkg
spctl --assess --type install --verbose=4 vela-0.2.0-macos-arm64.pkg
```

Verify a Linux signature:

```bash
gpg --import vela-linux-signing-key.asc
gpg --verify vela_0.2.0_amd64.deb.asc vela_0.2.0_amd64.deb
```

Finally, verify GitHub provenance for the standalone compiler:

```bash
gh attestation verify ./vela-0.2.0-linux-x64 --repo OWNER/REPOSITORY
```

GitHub artifact attestations require a public repository on Free, Pro, or Team,
or GitHub Enterprise Cloud for private/internal repositories. See GitHub's
[artifact attestation documentation](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
for availability and verification details.
