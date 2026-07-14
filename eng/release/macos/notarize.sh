#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <artifact-path>" >&2
  exit 64
fi

required=(APPLE_NOTARY_KEY_ID APPLE_NOTARY_ISSUER_ID APPLE_NOTARY_KEY_P8_BASE64)
for variable in "${required[@]}"; do
  if [[ -z "$(printenv "$variable" || true)" ]]; then
    echo "$variable is required for notarization." >&2
    exit 1
  fi
done

artifact_path="$1"
key_path="$RUNNER_TEMP/AuthKey_$APPLE_NOTARY_KEY_ID.p8"
trap 'rm -f "$key_path"' EXIT
base64 -D <<< "$APPLE_NOTARY_KEY_P8_BASE64" > "$key_path"
chmod 0600 "$key_path"

xcrun notarytool submit "$artifact_path" \
  --key "$key_path" \
  --key-id "$APPLE_NOTARY_KEY_ID" \
  --issuer "$APPLE_NOTARY_ISSUER_ID" \
  --wait
xcrun stapler staple "$artifact_path"
xcrun stapler validate "$artifact_path"
