#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || -z "${MACOS_APPLICATION_IDENTITY:-}" ]]; then
  echo "Usage: MACOS_APPLICATION_IDENTITY=<identity> $0 <binary-path>" >&2
  exit 64
fi

binary_path="$1"
codesign --force --options runtime --timestamp --sign "$MACOS_APPLICATION_IDENTITY" "$binary_path"
codesign --verify --deep --strict --verbose=2 "$binary_path"
