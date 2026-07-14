#!/usr/bin/env bash
set -euo pipefail

required=(
  MACOS_APPLICATION_CERTIFICATE_P12_BASE64
  MACOS_APPLICATION_CERTIFICATE_PASSWORD
  MACOS_INSTALLER_CERTIFICATE_P12_BASE64
  MACOS_INSTALLER_CERTIFICATE_PASSWORD
  MACOS_KEYCHAIN_PASSWORD
)
for variable in "${required[@]}"; do
  if [[ -z "$(printenv "$variable" || true)" ]]; then
    echo "$variable is required for a signed macOS release." >&2
    exit 1
  fi
done

keychain_path="$RUNNER_TEMP/vela-release.keychain-db"
application_certificate="$RUNNER_TEMP/vela-application.p12"
installer_certificate="$RUNNER_TEMP/vela-installer.p12"

base64 -D <<< "$MACOS_APPLICATION_CERTIFICATE_P12_BASE64" > "$application_certificate"
base64 -D <<< "$MACOS_INSTALLER_CERTIFICATE_P12_BASE64" > "$installer_certificate"

security create-keychain -p "$MACOS_KEYCHAIN_PASSWORD" "$keychain_path"
security set-keychain-settings -lut 21600 "$keychain_path"
security unlock-keychain -p "$MACOS_KEYCHAIN_PASSWORD" "$keychain_path"
security import "$application_certificate" -k "$keychain_path" -P "$MACOS_APPLICATION_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security import "$installer_certificate" -k "$keychain_path" -P "$MACOS_INSTALLER_CERTIFICATE_PASSWORD" -T /usr/bin/productsign
security set-key-partition-list -S apple-tool:,apple: -s -k "$MACOS_KEYCHAIN_PASSWORD" "$keychain_path"
security list-keychains -d user -s "$keychain_path"

rm -f "$application_certificate" "$installer_certificate"
echo "MACOS_SIGNING_KEYCHAIN=$keychain_path" >> "$GITHUB_ENV"
