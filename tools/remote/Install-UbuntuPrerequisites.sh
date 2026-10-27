#!/usr/bin/env bash
# Explicit WSL-root setup only; never changes host protection or Git policies.
set -eu
test -d /tmp
test "$(. /etc/os-release; printf %s "$ID:$VERSION_ID")" = ubuntu:24.04
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y ca-certificates curl wget git build-essential cmake ninja-build clang clang-18 llvm-18 libclang-rt-18-dev zlib1g-dev libssl-dev libicu-dev
if ! command -v pwsh >/dev/null; then
    stage=$(mktemp -d /tmp/velosim-packages.XXXXXX)
    wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O "$stage/packages.deb"
    dpkg -i "$stage/packages.deb"
    apt-get update
    apt-get install -y powershell
fi
