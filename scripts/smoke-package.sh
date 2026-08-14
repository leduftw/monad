#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: scripts/smoke-package.sh <rid> <archive> <expected-version>" >&2
  exit 64
fi

rid=$1
archive=$2
expected_version=$3

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-package-smoke.XXXXXX")
payload="$work_dir/payload"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

assert_pe_machine() {
  local file=$1
  local expected_machine=$2
  local expected_name=$3
  local offset0 offset1 offset2 offset3 pe_offset
  local sig0 sig1 sig2 sig3 machine0 machine1 machine

  read -r offset0 offset1 offset2 offset3 <<< "$(od -An -v -t u1 -j 60 -N 4 "$file")"

  if [[ -z ${offset3:-} ]]; then
    echo "could not read the PE header offset from $file" >&2
    exit 1
  fi

  pe_offset=$((offset0 + offset1 * 256 + offset2 * 65536 + offset3 * 16777216))
  read -r sig0 sig1 sig2 sig3 <<< "$(od -An -v -t u1 -j "$pe_offset" -N 4 "$file")"

  if [[ "$sig0 $sig1 $sig2 $sig3" != "80 69 0 0" ]]; then
    echo "$file does not contain a valid PE signature" >&2
    exit 1
  fi

  read -r machine0 machine1 <<< "$(od -An -v -t u1 -j "$((pe_offset + 4))" -N 2 "$file")"
  machine=$((machine0 + machine1 * 256))

  if [[ $machine -ne $expected_machine ]]; then
    printf '%s has PE machine 0x%04x, expected %s\n' "$file" "$machine" "$expected_name" >&2
    exit 1
  fi
}

mkdir -p "$payload"

case "$archive" in
  *.tar.gz)
    tar -xzf "$archive" -C "$payload"
    ;;
  *.zip)
    if command -v unzip >/dev/null 2>&1; then
      unzip -q "$archive" -d "$payload"
    elif command -v 7z >/dev/null 2>&1; then
      7z x -bd -o"$payload" "$archive" >/dev/null
    else
      echo "extracting $archive needs unzip or 7z" >&2
      exit 1
    fi
    ;;
  *)
    echo "unsupported archive: $archive" >&2
    exit 1
    ;;
esac

case "$rid" in
  osx-arm64 | osx-x64)
    executable="$payload/monad"
    expected_files=$'DOTNET-LICENSE.txt\nDOTNET-THIRD-PARTY-NOTICES.txt\nLICENSE\nREADME.md\nTHIRD-PARTY-NOTICES.md\nmonad\nmonad-audiotap\nmonad.example.json'
    ;;
  linux-arm64 | linux-x64)
    executable="$payload/monad"
    expected_files=$'DOTNET-LICENSE.txt\nDOTNET-THIRD-PARTY-NOTICES.txt\nLICENSE\nREADME.md\nTHIRD-PARTY-NOTICES.md\nmonad\nmonad.example.json'
    ;;
  win-arm64 | win-x64)
    executable="$payload/monad.exe"
    expected_files=$'DOTNET-LICENSE.txt\nDOTNET-THIRD-PARTY-NOTICES.txt\nLICENSE\nREADME.md\nTHIRD-PARTY-NOTICES.md\nmonad.exe\nmonad.example.json'
    ;;
  *)
    echo "unsupported runtime identifier: $rid" >&2
    exit 1
    ;;
esac

actual_files=$(CDPATH='' cd -- "$payload" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)

if [[ "$actual_files" != "$expected_files" ]]; then
  echo "unexpected files in $archive" >&2
  echo "expected:" >&2
  echo "$expected_files" >&2
  echo "actual:" >&2
  echo "$actual_files" >&2
  exit 1
fi

case "$rid" in
  win-arm64) assert_pe_machine "$executable" 43620 arm64 ;;
  win-x64) assert_pe_machine "$executable" 34404 x64 ;;
esac

actual_version=$("$executable" version)

if [[ "$actual_version" != "monad $expected_version" ]]; then
  echo "$executable reported '$actual_version'; expected 'monad $expected_version'" >&2
  exit 1
fi

"$executable" --config "$payload/monad.example.json" help >/dev/null

case "$rid" in
  osx-arm64 | osx-x64)
    "$payload/monad-audiotap" help >/dev/null
    "$executable" devices >/dev/null
    "$script_dir/smoke-posix-signals.sh" "$executable"
    ;;
  linux-arm64 | linux-x64)
    if ldd "$executable" | grep -Fq 'not found'; then
      echo "$executable has unresolved native libraries" >&2
      ldd "$executable" >&2
      exit 1
    fi

    "$script_dir/smoke-posix-signals.sh" "$executable"
    ;;
esac
