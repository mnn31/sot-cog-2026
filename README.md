# SoT CoG 2026 — Tales of Tribute AI Competition Entry

Entry for the [Tales of Tribute AI Competition](https://github.com/ScriptsOfTribute) at IEEE Conference on Games 2026.

- **Deadline:** August 10, 2026, 23:59 GMT (email submission to jko@cs.uni.wroc.pl)
- **Results:** announced at CoG, Sept 1–4, 2026
- **Format:** all-play-all mirror matches, random seeds, average win rate decides
- **Limits:** 10 s/turn, 256 MB steady memory (1 GB hard kill), 25 MB submission
- **Key rule:** decks/cards identical to 2025 edition; entry must be substantially different from other people's 2025 bots

## Benchmark target

2025 final standings (field win rate): SakkirinaSolo 71.8%, Sakkirina 70.6%, HQL_Plus_BOT 69.2%, BestMCTS3 66.1%. All sources are published in the competitions archive. Goal: beat SakkirinaSolo head-to-head over large seeded mirror-match sets.

## Layout

- `bot/` — our agent source (C#, compiled into the SoT-Core Bots project)
- `scripts/` — setup + round-robin benchmark harness
- `docs/` — engine API notes, past-winner analyses, experiment log

## Setup

```bash
scripts/setup.sh   # clones upstream repos, builds engine
```
