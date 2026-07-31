#!/usr/bin/env bash
# Sync bot/*.cs into the SoT-Core Bots project, rebuild, and stage the DLL where
# GameRunner actually loads bots from (<runner bin>/Bots/).
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="bot"
DEST="ScriptsOfTribute-Core/Bots/src/Riptide"
RUNNER_BIN="ScriptsOfTribute-Core/GameRunner/bin/Release/net8.0"

mkdir -p "$DEST"
rm -f "$DEST"/*.cs
cp "$SRC"/*.cs "$DEST"/

cd ScriptsOfTribute-Core
dotnet build TalesOfTribute.sln -c Release "$@" 2>&1 | tee /tmp/riptide_build.log | \
  grep -E "error|Riptide.*warning CS|Build succeeded|Build FAILED" | head -40 || true

if ! grep -q "Build succeeded" /tmp/riptide_build.log; then
  echo "BUILD FAILED — see /tmp/riptide_build.log"
  exit 1
fi

cd ..
mkdir -p "$RUNNER_BIN/Bots"
cp ScriptsOfTribute-Core/Bots/bin/Release/netstandard2.1/Bots.dll "$RUNNER_BIN/Bots/Bots.dll"
cp ScriptsOfTribute-Core/Bots/bin/Release/netstandard2.1/Bots.dll ScriptsOfTribute-Core/GameRunner/Bots.dll
echo "Riptide synced and built."
