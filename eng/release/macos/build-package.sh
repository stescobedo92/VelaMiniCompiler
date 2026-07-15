#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "Usage: $0 <version> <architecture> <publish-directory> <output-directory>" >&2
  exit 64
fi

version="$1"
architecture="$2"
publish_directory="$3"
output_directory="$4"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "The version must use X.Y.Z format." >&2
  exit 64
fi

compiler_path="$publish_directory/vela"
if [[ ! -x "$compiler_path" ]]; then
  echo "Expected executable '$compiler_path' was not found." >&2
  exit 1
fi
if [[ ! -f "$publish_directory/runtime/Vela.Runtime.csproj" || ! -f "$publish_directory/global.json" ]]; then
  echo "The staged compiler runtime support files are incomplete." >&2
  exit 1
fi

root="$(mktemp -d)"
scripts="$(mktemp -d)"
trap 'rm -rf "$root" "$scripts"' EXIT

install -d "$root/usr/local/lib/vela" "$root/usr/local/bin"
install -m 0755 "$compiler_path" "$root/usr/local/lib/vela/vela"
cp -R "$publish_directory/runtime" "$root/usr/local/lib/vela/runtime"
install -m 0644 "$publish_directory/global.json" "$root/usr/local/lib/vela/global.json"

cat > "$scripts/postinstall" <<'SCRIPT'
#!/bin/sh
set -eu
ln -sfn ../lib/vela/vela /usr/local/bin/vela
SCRIPT
chmod 0755 "$scripts/postinstall"

mkdir -p "$output_directory"
package_path="$output_directory/vela-$version-macos-$architecture.pkg"
pkgbuild \
  --root "$root" \
  --scripts "$scripts" \
  --identifier "dev.vela.compiler" \
  --version "$version" \
  --install-location / \
  "$package_path"

echo "$package_path"
