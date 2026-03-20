#!/usr/bin/env bash
# scripts/probe.sh — Send a Lua snippet to the game's debug API
# Requires SSH tunnel: ssh -L 1404:localhost:1404 windowspc
# Usage: ./scripts/probe.sh 'System.LogAlways("hello")'
#        ./scripts/probe.sh --read sv_servername
set -euo pipefail

GAME_API="${GAME_API:-http://localhost:1404}"

if [ "${1:-}" = "--read" ]; then
    CVAR="${2:?Usage: probe.sh --read <cvar_name>}"
    curl -s "$GAME_API/api/System/Console/GetCvarValue?name=$CVAR"
    echo ""
    exit 0
fi

LUA="${1:?Usage: probe.sh '<lua code>' or probe.sh --read <cvar>}"

ENCODED=$(python3 -c "import urllib.parse; print(urllib.parse.quote('#${LUA}'))")
RESULT=$(curl -s "$GAME_API/api/System/Console/ExecuteString?command=$ENCODED")
echo "$RESULT"
