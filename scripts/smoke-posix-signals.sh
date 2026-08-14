#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: scripts/smoke-posix-signals.sh <packaged-monad-binary>" >&2
  exit 64
fi

binary=$1
[[ -x "$binary" ]] || {
  echo "$binary is not executable" >&2
  exit 1
}

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/monad-signal-smoke.XXXXXX")
output="$work_dir/output.log"
pid=

cleanup() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill -KILL "$pid" 2>/dev/null || true
  fi

  rm -rf -- "$work_dir"
}
trap cleanup EXIT

"$binary" run --no-recognize >"$output" 2>&1 &
pid=$!

# Give Monad enough time to install its signal handlers and start the capture
# pump. The capture helper is allowed to fail on a headless CI runner; the host
# process must still stay alive and shut down gracefully.
sleep 2

if ! kill -TERM "$pid" 2>/dev/null; then
  echo "Monad exited before the SIGTERM smoke test could signal it" >&2
  cat "$output" >&2
  exit 1
fi

for _ in {1..100}; do
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.1
done

if kill -0 "$pid" 2>/dev/null; then
  echo "Monad did not stop within 10 seconds of SIGTERM" >&2
  cat "$output" >&2
  exit 1
fi

set +e
wait "$pid"
status=$?
set -e
pid=

if [[ $status -ne 0 ]]; then
  echo "Monad exited with status $status after SIGTERM" >&2
  cat "$output" >&2
  exit 1
fi

if ! grep -Fq 'Stopped.' "$output"; then
  echo "Monad did not print its graceful-shutdown summary" >&2
  cat "$output" >&2
  exit 1
fi
