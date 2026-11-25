#!/usr/bin/env bash
set -euo pipefail
if ! command -v python3 >/dev/null; then
  elevated=()
  if [ "$(id -u)" != 0 ]; then elevated=(sudo -n); fi
  if command -v apt-get >/dev/null; then
    "${elevated[@]}" apt-get update
    "${elevated[@]}" apt-get install -y python3 ca-certificates
  elif command -v dnf >/dev/null; then
    "${elevated[@]}" dnf install -y python3 ca-certificates
  else
    printf '%s\n' 'Install Python 3.10+ and CA certificates before deployment.' >&2
    exit 1
  fi
fi
python3 -c 'import sys; assert sys.version_info >= (3,10), "Python 3.10+ required"'
printf '%s\n' 'Environment ready.'
