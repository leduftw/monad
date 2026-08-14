#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: scripts/smoke-installer.sh <artifact-directory>" >&2
  exit 64
fi

artifact_dir=$(CDPATH='' cd -- "$1" && pwd)
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
install_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-installer-smoke.XXXXXX")
server_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-installer-server.XXXXXX")
server_pid=

cleanup() {
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi

  rm -rf -- "$install_dir"
  rm -rf -- "$server_dir"
}
trap cleanup EXIT

case $(uname -s) in
  Darwin) asset="monad-macos-$(uname -m | sed 's/^x86_64$/x64/').zip" ;;
  Linux)
    machine=$(uname -m)
    [[ "$machine" == aarch64 ]] && machine=arm64
    [[ "$machine" == x86_64 ]] && machine=x64
    asset="monad-linux-$machine.tar.gz"
    ;;
  *)
    echo "installer smoke test supports macOS and Linux" >&2
    exit 1
    ;;
esac

[[ -f "$artifact_dir/$asset" ]] || {
  echo "$artifact_dir/$asset does not exist" >&2
  exit 1
}

cp "$artifact_dir/$asset" "$server_dir/$asset"

if command -v sha256sum >/dev/null 2>&1; then
  (CDPATH='' cd -- "$server_dir" && sha256sum "$asset" > SHA256SUMS)
else
  checksum=$(shasum -a 256 "$server_dir/$asset" | awk '{ print $1 }')
  printf '%s  %s\n' "$checksum" "$asset" > "$server_dir/SHA256SUMS"
fi

port=$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')

python3 -m http.server "$port" --bind 127.0.0.1 --directory "$server_dir" \
  >"$install_dir/server.log" 2>&1 &
server_pid=$!

for _ in {1..50}; do
  if curl --fail --silent "http://127.0.0.1:$port/SHA256SUMS" >/dev/null; then
    break
  fi
  sleep 0.1
done

curl --fail --silent "http://127.0.0.1:$port/SHA256SUMS" >/dev/null

MONAD_DOWNLOAD_BASE="http://127.0.0.1:$port" \
MONAD_INSTALL_DIR="$install_dir/bin" \
  "$repo_root/install.sh" >/dev/null

# A second pass exercises the staged replacement path used by upgrades.
MONAD_DOWNLOAD_BASE="http://127.0.0.1:$port" \
MONAD_INSTALL_DIR="$install_dir/bin" \
  "$repo_root/install.sh" >/dev/null

"$install_dir/bin/monad" version >/dev/null

if [[ $(uname -s) == Darwin ]]; then
  "$install_dir/bin/monad-audiotap" help >/dev/null
fi
