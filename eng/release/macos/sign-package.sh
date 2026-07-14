#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || -z "${MACOS_INSTALLER_IDENTITY:-}" ]]; then
  echo "Usage: MACOS_INSTALLER_IDENTITY=<identity> $0 <package-path>" >&2
  exit 64
fi

package_path="$1"
package_directory="$(dirname "$package_path")"
package_base_name="$(basename "$package_path" .pkg)"
signed_package="$package_directory/$package_base_name-signed.pkg"
productsign --sign "$MACOS_INSTALLER_IDENTITY" "$package_path" "$signed_package"
mv "$signed_package" "$package_path"
pkgutil --check-signature "$package_path"
