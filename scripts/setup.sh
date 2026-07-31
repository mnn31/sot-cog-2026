#!/usr/bin/env bash
# Clone upstream Scripts of Tribute repos and build the engine.
set -euo pipefail
cd "$(dirname "$0")/.."

for repo in ScriptsOfTribute-Core ScriptsOfTribute-CompetitionsArchive ScriptsOfTribute-Python; do
  [ -d "$repo" ] || git clone --depth 1 "https://github.com/ScriptsOfTribute/$repo.git"
done

cd ScriptsOfTribute-Core
dotnet build TalesOfTribute.sln -c Release
