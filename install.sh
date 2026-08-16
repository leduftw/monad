#!/bin/sh

# The body of this installer lives in main, which is called on the very last
# line. README tells people to pipe this script into sh, and a shell reading a
# pipe executes each command as it arrives: a connection that drops midway
# would otherwise run whichever prefix of the script had arrived. Reaching the
# end of a truncated download without the final call means nothing runs at all.

set -eu

work_dir=
staged_main=
staged_helper=

cleanup() {
  [ -z "$staged_main" ] || rm -f -- "$staged_main"
  [ -z "$staged_helper" ] || rm -f -- "$staged_helper"
  [ -z "$work_dir" ] || rm -rf -- "$work_dir"
}

main() {
  repository=${MONAD_REPOSITORY:-leduftw/monad}
  requested_version=${MONAD_VERSION:-latest}

  command -v curl >/dev/null 2>&1 || {
    echo "monad installer: curl is required" >&2
    exit 1
  }

  case $(uname -s) in
    Darwin)
      platform=macos
      archive_extension=zip
      ;;
    Linux)
      platform=linux
      archive_extension=tar.gz
      ;;
    *)
      echo "monad installer: unsupported operating system: $(uname -s)" >&2
      exit 1
      ;;
  esac

  if [ "$platform" = macos ]; then
    macos_version=$(sw_vers -productVersion)
    macos_major=${macos_version%%.*}
    macos_remainder=${macos_version#*.}

    if [ "$macos_remainder" = "$macos_version" ]; then
      macos_minor=0
    else
      macos_minor=${macos_remainder%%.*}
    fi

    case "$macos_major:$macos_minor" in
      *[!0-9:]*)
        echo "monad installer: could not read the macOS version: $macos_version" >&2
        exit 1
        ;;
    esac

    if [ "$macos_major" -lt 14 ] \
      || { [ "$macos_major" -eq 14 ] && [ "$macos_minor" -lt 2 ]; }; then
      echo "monad installer: macOS 14.2 or newer is required" >&2
      exit 1
    fi
  elif [ -e /etc/alpine-release ] \
    || { command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; }; then
    echo "monad installer: the prebuilt Linux release requires glibc; musl/Alpine is not yet supported" >&2
    exit 1
  fi

  case $(uname -m) in
    arm64 | aarch64)
      architecture=arm64
      ;;
    x86_64 | amd64)
      architecture=x64
      ;;
    *)
      echo "monad installer: unsupported architecture: $(uname -m)" >&2
      exit 1
      ;;
  esac

  if [ -n "${MONAD_DOWNLOAD_BASE:-}" ]; then
    download_base=${MONAD_DOWNLOAD_BASE%/}
  elif [ "$requested_version" = latest ]; then
    download_base="https://github.com/$repository/releases/latest/download"
  else
    version=${requested_version#v}
    download_base="https://github.com/$repository/releases/download/v$version"
  fi

  asset="monad-$platform-$architecture.$archive_extension"

  if [ -n "${MONAD_INSTALL_DIR:-}" ]; then
    install_dir=$MONAD_INSTALL_DIR
  elif [ -d /usr/local/bin ] && [ -w /usr/local/bin ]; then
    install_dir=/usr/local/bin
  else
    : "${HOME:?HOME must be set, or set MONAD_INSTALL_DIR explicitly}"
    install_dir=$HOME/.local/bin
  fi

  work_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-install.XXXXXX")
  trap cleanup EXIT HUP INT TERM

  echo "Downloading $asset..."
  curl --fail --silent --show-error --location \
    "$download_base/$asset" \
    --output "$work_dir/$asset"
  curl --fail --silent --show-error --location \
    "$download_base/SHA256SUMS" \
    --output "$work_dir/SHA256SUMS"

  expected=$(awk -v asset="$asset" '$2 == asset || $2 == "*" asset { print $1; exit }' "$work_dir/SHA256SUMS")

  if [ -z "$expected" ]; then
    echo "monad installer: SHA256SUMS has no entry for $asset" >&2
    exit 1
  fi

  if command -v sha256sum >/dev/null 2>&1; then
    actual=$(sha256sum "$work_dir/$asset" | awk '{ print $1 }')
  elif command -v shasum >/dev/null 2>&1; then
    actual=$(shasum -a 256 "$work_dir/$asset" | awk '{ print $1 }')
  else
    echo "monad installer: sha256sum or shasum is required to verify the download" >&2
    exit 1
  fi

  if [ "$actual" != "$expected" ]; then
    echo "monad installer: checksum verification failed for $asset" >&2
    exit 1
  fi

  payload="$work_dir/payload"
  mkdir -p "$payload"

  if [ "$archive_extension" = zip ]; then
    ditto -x -k "$work_dir/$asset" "$payload"
  else
    tar -xzf "$work_dir/$asset" -C "$payload"
  fi

  if [ ! -f "$payload/monad" ]; then
    echo "monad installer: $asset did not contain monad" >&2
    exit 1
  fi

  if [ "$platform" = macos ]; then
    if [ ! -f "$payload/monad-audiotap" ]; then
      echo "monad installer: $asset did not contain monad-audiotap" >&2
      exit 1
    fi
  fi

  mkdir -p "$install_dir"
  staged_main="$install_dir/.monad.$$.new"
  install -m 755 "$payload/monad" "$staged_main"
  installed_version=$("$staged_main" version)

  if [ "$platform" = macos ]; then
    staged_helper="$install_dir/.monad-audiotap.$$.new"
    install -m 755 "$payload/monad-audiotap" "$staged_helper"
    mv -f "$staged_helper" "$install_dir/monad-audiotap"
    staged_helper=
  fi

  mv -f "$staged_main" "$install_dir/monad"
  staged_main=
  echo "Installed $installed_version in $install_dir"

  case :${PATH:-}: in
    *:"$install_dir":*) ;;
    *)
      echo "Add $install_dir to PATH, then open a new terminal:"
      echo "  export PATH=\"$install_dir:\$PATH\""
      ;;
  esac

  if [ "$platform" = linux ] \
    && ! command -v parec >/dev/null 2>&1 \
    && ! command -v ffmpeg >/dev/null 2>&1; then
    echo "Linux capture also needs parec (pulseaudio-utils) or ffmpeg."
  fi

  echo "Set AUDD_API_TOKEN, then run: monad"
}

main "$@"
