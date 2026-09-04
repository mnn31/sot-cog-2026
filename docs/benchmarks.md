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

## Submission gauntlet (2026-08-03, snapshot v2 = submission candidate, Mac, validity-gated)

| Opponent | 2025 rank | Clean result | Notes |
|---|---|---|---|
| SakkirinaSolo | 1st (71.8%) | **68.8%** (11/16) | clean batch, 0 flags |
| Sakkirina | 2nd (70.6%) | ~58–62% clean-only | opponent dies to timeout/crash in up to 50% of games on this host (all abnormal ends were its losses; forensics seeds 9200-9209); env-specific fragility, clean-games number reported |
| HQL_Plus_BOT | 3rd (69.2%) | 68.8% (11/16) | clean |
| BestMCTS3 | 4th (66.1%) | 50.0% (8/16) | clean |
| EBot_C | 5th (56.0%) | 81.2% (13/16) | clean |
| EBot_F | 6th (51.8%) | 68.8% (11/16) | clean |

Field win rate: **66.7%** on both independent 96-game halves (128/192 total, CI 59.7–72.9). 2025 champion's field number: 71.8% (vs a field diluted by weak Python entrants not present here).

## OFFICIAL RESULT — IEEE CoG 2026 (announced 2026-09-04)

**Primal Kyogre placed 2nd** (prize podium) — 3rd in the raw table, behind two entries
by the same author (one prize per participant).

| # | Agent | Winrate | Games | W | L |
|---|---|---|---|---|---|
| 1 | DeepSetsBlendBot (Doruk Karaman) | 69.21% | 5460 | 3779 | 1681 |
| 2 | DeepSetsBot (Doruk Karaman) | 68.24% | 5460 | 3726 | 1734 |
| **3** | **Primal Kyogre (Manan Gupta)** | **55.16%** | **5460** | **3012** | **2448** |
| 4 | SakkirinaSolo (2025 champion, benchmark) | 48.97% | 5460 | 2674 | 2786 |
| 5 | NAgent_4 (Niklas Angert) | 46.25% | 5460 | 2525 | 2935 |
| 6 | BestMCTS3 (2025, benchmark) | 44.51% | 5460 | 2430 | 3030 |
| 7 | HQL_Plus_BOT (2025, benchmark) | 44.30% | 5460 | 2419 | 3041 |
| 8 | PatronZeroBot (Martin Niederl) | 23.35% | 5460 | 1275 | 4185 |

Prize podium: 1st DeepSetsBlendBot, **2nd Primal Kyogre**, 3rd NAgent_4.

Official conditions: 8 games per pair per round, 5,460 games per agent, average winrate;
Ubuntu 25.04, .NET 8.0, 8 CPUs, 32 GB RAM.

### Prediction vs. reality

- **Goal achieved:** we finished above all three 2025 podium agents — SakkirinaSolo (4th),
  BestMCTS3 (6th), HQL_Plus_BOT (7th). Our pre-submission gauntlet predicted exactly this.
- **Field winrate came in lower than predicted** (55.16% actual vs 66.7% local): our local
  field was the 2025 agents only. The two 2026 entries above us were new — a learned
  evaluation network grafted onto SakkirinaSolo's search, i.e. precisely the "train the
  evaluation" step identified in riptide-v1 §3 as the highest-value next move, which we
  never reached (pod loss ended the tuning campaign).
- Zero abnormal endings attributable to our agent across 5,460 official games.
