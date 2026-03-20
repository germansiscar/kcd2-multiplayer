#!/usr/bin/env bash
# scripts/build.sh — Build and test on Mac
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")/../dotnet"

echo "=== Building ==="
dotnet build

echo ""
echo "=== Running Tests ==="
dotnet test

echo ""
echo "=== Build + Test OK ==="
