#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
git submodule sync --recursive
git submodule update --init --recursive external/Surelog external/UHDM
surelog_sha="$(git -C external/Surelog rev-parse HEAD)"
uhdm_sha="$(git -C external/UHDM rev-parse HEAD)"
nested_sha="$(git -C external/Surelog/third_party/UHDM rev-parse HEAD)"
printf 'Surelog: %s\nStandalone UHDM: %s\nSurelog nested UHDM: %s\n' "$surelog_sha" "$uhdm_sha" "$nested_sha"
if [[ "$uhdm_sha" != "$nested_sha" ]]; then
  echo "Error: standalone UHDM and Surelog's nested UHDM differ" >&2
  exit 1
fi
echo "UHDM revisions match. Build Surelog using its nested UHDM dependency."
