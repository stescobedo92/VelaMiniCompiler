#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || -z "${MACOS_APPLICATION_IDENTITY:-}" ]]; then
  echo "Usage: MACOS_APPLICATION_IDENTITY=<identity> $0 <dmg-path>" >&2
  exit 64
fi

dmg_path="$1"
codesign --force --timestamp --sign "$MACOS_APPLICATION_IDENTITY" "$dmg_path"
codesign --verify --verbose=2 "$dmg_path"
