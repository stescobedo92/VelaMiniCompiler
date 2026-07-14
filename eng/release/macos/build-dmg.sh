#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-path> <output-directory>" >&2
  exit 64
fi

package_path="$1"
output_directory="$2"

if [[ ! -f "$package_path" ]]; then
  echo "Package '$package_path' was not found." >&2
  exit 1
fi

package_name="$(basename "$package_path")"
dmg_base_name="$(basename "$package_path" .pkg)"
dmg_path="$output_directory/$dmg_base_name.dmg"
staging_directory="$(mktemp -d)"
trap 'rm -rf "$staging_directory"' EXIT

cp "$package_path" "$staging_directory/$package_name"
cat > "$staging_directory/README.txt" <<'TEXT'
Open the PKG installer, then open a new terminal and run `vela --help`.
The installer places the compiler in /usr/local/lib/vela and exposes `vela`
globally through /usr/local/bin.
TEXT
rm -f "$dmg_path"
hdiutil create \
  -volname "Vela Compiler" \
  -srcfolder "$staging_directory" \
  -ov \
  -format UDZO \
  "$dmg_path"

echo "$dmg_path"
