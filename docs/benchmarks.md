# Benchmark log

All runs: seeded mirror pairs (same seed, seats swapped), `-to 10` (competition time control), frozen-snapshot runner. CI = Wilson 95%.

| Date | Version | Opponent | Result | WR | 95% CI | Seat split (P1/P2) | Seeds |
|---|---|---|---|---|---|---|---|
| 2026-07-31 | Squirtle (v1, snapshot v1, class `Riptide`) | MCTSBot (built-in) | 12–0 | 100% | — | 6/6 | 200–202, 900–902 |
| 2026-07-31 | Squirtle (v1) | BestMCTS3 (2025 #4) | 25–23 | 52.1% | 38.3–65.5% | 16/9 | 3000–3023 |
| 2026-07-31 | Squirtle (v1) | SakkirinaSolo (2025 champion) | 24–24 | 50.0% | 36.4–63.6% | 15/9 | 4000–4023 |

2025 field reference (win rate vs whole field): SakkirinaSolo 71.8%, Sakkirina 70.6%, HQL_Plus_BOT 69.2%, BestMCTS3 66.1%.

## Version line

- **Squirtle** = v1 baseline: K=14 determinization ensemble, PUCT turn-plan search with max backup, hand-set eval weights, deliberate patron draft (Orgnum-first). Frozen at `snapshots/v1/`.
- **Wartortle** = pending: v1 + SPSA-tuned eval weights (in progress). Promotion requires beating Squirtle head-to-head and improving vs BestMCTS3/SakkirinaSolo on fresh seeds.

## Open questions

- Seat asymmetry: Squirtle wins ~65% as P1 but ~38% as P2 in both baseline runs (n=24 each; pattern consistent). Check second-seat compensation handling in eval (tempo/break-even prestige term) and whether the field shows the same P1 skew on these seeds.
