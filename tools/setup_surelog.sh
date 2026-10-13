#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
if [[ ! -f .gitmodules ]]; then
  echo 'Error: run from a VeloSim checkout with the Surelog submodule.' >&2
  exit 1
fi
git submodule sync --recursive
git submodule update --init --recursive external/Surelog
printf 'Surelog checkout: '
git -C external/Surelog rev-parse HEAD
printf 'UHDM checkout: '
find external/Surelog -maxdepth 4 -name UHDM -type d -print | head -n 1
