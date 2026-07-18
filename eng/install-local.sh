#!/usr/bin/env bash
# Publishes a local Native AOT `vela` compiler and installs a symlink into ~/.local/bin.
set -euo pipefail

repository_root="$(cd "$(dirname "$0")/.." && pwd)"
prefix="${VELA_PREFIX:-$HOME/.local/lib/vela}"
bin_link_dir="${VELA_BIN_DIR:-$HOME/.local/bin}"
rid="${VELA_RID:-}"

if [[ -z "$rid" ]]; then
  case "$(uname -s)" in
    Darwin)
      case "$(uname -m)" in
        arm64) rid="osx-arm64" ;;
        *) rid="osx-x64" ;;
      esac
      ;;
    Linux) rid="linux-x64" ;;
    *) echo "Unsupported host for install-local.sh" >&2; exit 1 ;;
  esac
fi

publish_directory="$prefix"
mkdir -p "$publish_directory" "$bin_link_dir"

echo "Publishing vela to $publish_directory ($rid)..."
dotnet publish "$repository_root/src/Vela.Cli/Vela.Cli.csproj" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:PublishAot=true \
  -p:InvariantGlobalization=true \
  --output "$publish_directory" \
  --nologo

pwsh -File "$repository_root/eng/release/stage-runtime.ps1" -PublishDirectory "$publish_directory"

if [[ -f "$publish_directory/Vela.Cli" && ! -f "$publish_directory/vela" ]]; then
  mv "$publish_directory/Vela.Cli" "$publish_directory/vela"
fi

chmod +x "$publish_directory/vela"
ln -sfn "$publish_directory/vela" "$bin_link_dir/vela"

echo
echo "Installed: $publish_directory/vela"
echo "Symlink:   $bin_link_dir/vela"
echo "Ensure $bin_link_dir is on your PATH, then run:"
echo "  vela --help"
