#!/usr/bin/env bash

set -euo pipefail

usage() {
  echo "usage: scripts/package-release.sh <rid> <output-directory> [expected-version]" >&2
  exit 64
}

[[ $# -ge 2 && $# -le 3 ]] || usage

rid=$1
output_dir=$2
expected_version=${3:-}

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
project="$repo_root/Monad/Monad.csproj"
dotnet_command=${DOTNET_COMMAND:-dotnet}

version=$(sed -n 's:.*<Version>\([^<][^<]*\)</Version>.*:\1:p' "$project")
[[ -n "$version" && "$version" != *$'\n'* ]] || {
  echo "could not read one <Version> from $project" >&2
  exit 1
}

if [[ -n "$expected_version" && "$version" != "$expected_version" ]]; then
  echo "project version $version does not match expected version $expected_version" >&2
  exit 1
fi

case "$rid" in
  osx-arm64)
    platform=macos
    architecture=arm64
    executable=monad
    archive_format=zip
    ;;
  osx-x64)
    platform=macos
    architecture=x64
    executable=monad
    archive_format=zip
    ;;
  linux-arm64)
    platform=linux
    architecture=arm64
    executable=monad
    archive_format=tar
    ;;
  linux-x64)
    platform=linux
    architecture=x64
    executable=monad
    archive_format=tar
    ;;
  win-arm64)
    platform=windows
    architecture=arm64
    executable=monad.exe
    archive_format=zip
    ;;
  win-x64)
    platform=windows
    architecture=x64
    executable=monad.exe
    archive_format=zip
    ;;
  *)
    echo "unsupported runtime identifier: $rid" >&2
    usage
    ;;
esac

if [[ "$platform" == macos && $(uname -s) != Darwin ]]; then
  echo "$rid must be packaged on macOS because it includes the Swift capture helper" >&2
  exit 1
fi

mkdir -p "$output_dir"
output_dir=$(CDPATH='' cd -- "$output_dir" && pwd)
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-package.XXXXXX")
publish_dir="$work_dir/publish"
stage_dir="$work_dir/stage"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

publish_properties=(
  -p:PublishSingleFile=true
  -p:SelfContained=true
  -p:IncludeNativeLibrariesForSelfExtract=true
  # The release archive supplies transport compression. Keeping the embedded
  # runtime uncompressed avoids a reproducible .NET 10 pipe crash on macOS 26
  # and also improves startup time.
  -p:EnableCompressionInSingleFile=false
  -p:PublishTrimmed=false
  -p:DebugSymbols=false
  -p:DebugType=None
  -p:IncludeSourceRevisionInInformationalVersion=false
  -p:RestoreLockedMode=true
  -p:ContinuousIntegrationBuild=true
)

if [[ "$platform" == macos ]]; then
  publish_properties+=(
    -p:RequireAudioTapSidecar=true
    -p:SkipAudioTapSidecar=false
  )
else
  publish_properties+=(
    -p:SkipAudioTapSidecar=true
  )
fi

"$dotnet_command" publish "$project" \
  --configuration Release \
  --runtime "$rid" \
  --output "$publish_dir" \
  "${publish_properties[@]}"

[[ -f "$publish_dir/$executable" ]] || {
  echo "publish did not produce $executable for $rid" >&2
  exit 1
}

mkdir -p "$stage_dir"
install -m 755 "$publish_dir/$executable" "$stage_dir/$executable"

if [[ "$platform" == macos ]]; then
  helper="$publish_dir/monad-audiotap"
  [[ -f "$helper" ]] || {
    echo "publish did not produce monad-audiotap for $rid" >&2
    exit 1
  }

  expected_macho_arch=$architecture
  [[ "$architecture" == x64 ]] && expected_macho_arch=x86_64

  for binary in "$publish_dir/monad" "$helper"; do
    actual_archs=$(lipo -archs "$binary")
    [[ " $actual_archs " == *" $expected_macho_arch "* ]] || {
      echo "$binary has architecture '$actual_archs', expected $expected_macho_arch" >&2
      exit 1
    }

    if otool -L "$binary" | grep -Eq '/opt/homebrew/|/usr/local/(Cellar|opt)/'; then
      echo "$binary links to a package-manager path and is not portable:" >&2
      otool -L "$binary" >&2
      exit 1
    fi
  done

  install -m 755 "$helper" "$stage_dir/monad-audiotap"

  # Cross-compiled Swift output is not always linker-signed, so seal the small
  # helper explicitly. Preserve dotnet's existing host signature: re-signing a
  # .NET single-file bundle with generic codesign options changes its Mach-O
  # executable region and can break the embedded runtime. Developer ID signing
  # must use the documented .NET entitlements when it is added.
  codesign --force --sign - "$stage_dir/monad-audiotap"
  codesign --verify --strict "$stage_dir/monad-audiotap"
  codesign --verify --strict "$stage_dir/monad"
fi

install -m 644 "$repo_root/LICENSE" "$stage_dir/LICENSE"
install -m 644 "$repo_root/README.md" "$stage_dir/README.md"
install -m 644 "$repo_root/THIRD-PARTY-NOTICES.md" "$stage_dir/THIRD-PARTY-NOTICES.md"
install -m 644 "$repo_root/monad.example.json" "$stage_dir/monad.example.json"

dotnet_root=${DOTNET_ROOT:-}

# setup-dotnet exposes a Unix-style executable path even under Git Bash, while
# `dotnet --info` prints a native Windows path that POSIX dirname cannot parse.
# Prefer the executable's directory whenever it is a complete SDK root.
dotnet_executable=$(command -v "$dotnet_command" || true)

if [[ (-z "$dotnet_root" || ! -f "$dotnet_root/LICENSE.txt") && -n "$dotnet_executable" ]]; then
  dotnet_directory=$(dirname -- "$dotnet_executable")

  if [[ -f "$dotnet_directory/LICENSE.txt" ]]; then
    dotnet_root=$dotnet_directory
  fi
fi

if [[ -z "$dotnet_root" || ! -f "$dotnet_root/LICENSE.txt" ]]; then
  dotnet_base=$(DOTNET_CLI_UI_LANGUAGE=en "$dotnet_command" --info \
    | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' \
    | head -n 1)
  dotnet_root=$(dirname -- "$(dirname -- "${dotnet_base%/}")")
fi

for notice in LICENSE.txt ThirdPartyNotices.txt; do
  [[ -f "$dotnet_root/$notice" ]] || {
    echo "the release SDK does not provide $notice under $dotnet_root" >&2
    echo "use the official Microsoft .NET SDK for release packaging" >&2
    exit 1
  }
done

install -m 644 "$dotnet_root/LICENSE.txt" "$stage_dir/DOTNET-LICENSE.txt"
install -m 644 "$dotnet_root/ThirdPartyNotices.txt" "$stage_dir/DOTNET-THIRD-PARTY-NOTICES.txt"

asset="monad-$platform-$architecture"

if [[ "$archive_format" == tar ]]; then
  archive="$output_dir/$asset.tar.gz"
  rm -f -- "$archive"
  tar -czf "$archive" -C "$stage_dir" .
else
  archive="$output_dir/$asset.zip"
  rm -f -- "$archive"

  if command -v 7z >/dev/null 2>&1; then
    (CDPATH='' cd -- "$stage_dir" && 7z a -bd -tzip "$archive" ./* >/dev/null)
  elif command -v zip >/dev/null 2>&1; then
    (CDPATH='' cd -- "$stage_dir" && zip -q "$archive" ./*)
  else
    echo "packaging $rid needs either 7z or zip" >&2
    exit 1
  fi
fi

echo "$archive"
