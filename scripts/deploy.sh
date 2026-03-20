#!/usr/bin/env bash
# scripts/deploy.sh — Cross-compile for Windows and deploy via SCP
# Usage: ./scripts/deploy.sh user@windowspc [remote_dir]
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH"

REMOTE="${1:-}"
REMOTE_DIR="${2:-C:/kcdmp}"

if [ -z "$REMOTE" ]; then
    echo "Usage: deploy.sh user@windowspc [remote_dir]"
    echo "  Builds win-x64 single-file exe and copies to Windows PC via SCP"
    exit 1
fi

cd "$(dirname "$0")/../dotnet"

echo "=== Cross-compiling for win-x64 ==="
dotnet publish KcdMp.App -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/app

echo ""
echo "=== Deploying to $REMOTE:$REMOTE_DIR ==="
ssh "$REMOTE" "mkdir -p '$REMOTE_DIR'" 2>/dev/null || true
scp publish/app/kcdmp.exe "$REMOTE:$REMOTE_DIR/"

echo "=== Deploying mod files ==="
scp -r ../kdcmp "$REMOTE:$REMOTE_DIR/"

echo ""
echo "=== Deploy complete ==="
echo "On Windows: cd $REMOTE_DIR && kcdmp.exe"
