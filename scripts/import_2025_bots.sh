#!/usr/bin/env bash
# Import 2025 competition C# bots into the SoT-Core Bots project so GameRunner
# can play them. Each bot gets its own namespace (Arch<Name>) to avoid class
# collisions (they all shipped as `namespace Bots` with overlapping helper names).
set -euo pipefail
cd "$(dirname "$0")/.."

ARCHIVE="ScriptsOfTribute-CompetitionsArchive/competition-2025-08-COG/cs_agents"
DEST="ScriptsOfTribute-Core/Bots/src/Archive2025"
BOTS=(BestMCTS3 EBot_C EBot_F HQL_Plus_BOT Sakkirina SakkirinaSolo)

rm -rf "$DEST"
mkdir -p "$DEST"

for bot in "${BOTS[@]}"; do
  mkdir -p "$DEST/$bot"
  for f in "$ARCHIVE/$bot"/*.cs; do
    sed -e "s/^namespace Bots;/namespace Arch${bot};/" \
        -e "s/global using EBW = Bots\./global using EBW = Arch${bot}./" \
        "$f" > "$DEST/$bot/$(basename "$f")"
  done
done

cd ScriptsOfTribute-Core
dotnet build TalesOfTribute.sln -c Release 2>&1 | grep -E "error|Error|Build succeeded" | head -20 || true

# GameRunner loads bot DLLs from <bin>/Bots/ (it ships a stale prebuilt
# Bots.dll via a csproj link) — overwrite with the freshly built one.
RUNNER_BIN="GameRunner/bin/Release/net8.0"
mkdir -p "$RUNNER_BIN/Bots"
cp Bots/bin/Release/netstandard2.1/Bots.dll "$RUNNER_BIN/Bots/Bots.dll"
cp Bots/bin/Release/netstandard2.1/Bots.dll GameRunner/Bots.dll

# HQL_Plus_BOT reads Q-tables from <GameRunner bin>/Bots/
cp "../$ARCHIVE/HQL_Plus_BOT/QTABLE1.txt" "../$ARCHIVE/HQL_Plus_BOT/QTABLE2.txt" "$RUNNER_BIN/Bots/"
echo "Import done."
