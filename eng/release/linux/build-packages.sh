#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <version> <publish-directory> <output-directory>" >&2
  exit 64
fi

version="$1"
publish_directory="$2"
output_directory="$3"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "The version must use X.Y.Z format." >&2
  exit 64
fi

compiler_path="$publish_directory/vela"
if [[ ! -x "$compiler_path" ]]; then
  echo "Expected executable '$compiler_path' was not found." >&2
  exit 1
fi

mkdir -p "$output_directory"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
install -d "$stage/usr/local/lib/vela" "$stage/usr/local/bin"
install -m 0755 "$compiler_path" "$stage/usr/local/lib/vela/vela"
ln -s ../lib/vela/vela "$stage/usr/local/bin/vela"

repository="$GITHUB_REPOSITORY"
if [[ -z "$repository" ]]; then
  repository="vela-lang/vela"
fi

fpm --force \
  --input-type dir \
  --output-type deb \
  --name vela \
  --version "$version" \
  --architecture amd64 \
  --description "Vela compiler and command-line tools" \
  --url "https://github.com/$repository" \
  --license "MIT" \
  --package "$output_directory/vela_$version_amd64.deb" \
  -C "$stage" .

fpm --force \
  --input-type dir \
  --output-type rpm \
  --name vela \
  --version "$version" \
  --architecture x86_64 \
  --description "Vela compiler and command-line tools" \
  --url "https://github.com/$repository" \
  --license "MIT" \
  --package "$output_directory/vela-$version-1.x86_64.rpm" \
  -C "$stage" .
