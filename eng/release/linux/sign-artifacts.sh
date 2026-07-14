#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  echo "Usage: $0 <artifact> [<artifact> ...]" >&2
  exit 64
fi

if [[ -z "${LINUX_GPG_PRIVATE_KEY:-}" || -z "${LINUX_GPG_KEY_ID:-}" ]]; then
  echo "LINUX_GPG_PRIVATE_KEY and LINUX_GPG_KEY_ID are required for a signed Linux release." >&2
  exit 1
fi

export GNUPGHOME
GNUPGHOME="$(mktemp -d)"
chmod 0700 "$GNUPGHOME"
trap 'rm -rf "$GNUPGHOME"' EXIT

printf '%s' "$LINUX_GPG_PRIVATE_KEY" | gpg --batch --import
artifact_directory="$(dirname "$1")"
gpg --batch --armor --export "$LINUX_GPG_KEY_ID" > "$artifact_directory/vela-linux-signing-key.asc"
if [[ ! -s "$artifact_directory/vela-linux-signing-key.asc" ]]; then
  echo "Unable to export the configured Linux signing public key." >&2
  exit 1
fi

arguments=(--batch --yes --armor --local-user "$LINUX_GPG_KEY_ID" --detach-sign)
if [[ -n "${LINUX_GPG_PASSPHRASE:-}" ]]; then
  arguments+=(--pinentry-mode loopback --passphrase "$LINUX_GPG_PASSPHRASE")
fi

for artifact in "$@"; do
  gpg "${arguments[@]}" "$artifact"
  gpg --verify "$artifact.asc" "$artifact"
done
