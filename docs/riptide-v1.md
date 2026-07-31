# Riptide v1

Our CoG 2026 Tales of Tribute entry. Source lives in `bot/` (`namespace Riptide`,
main class `Riptide : AI`) and is synced into the engine by `scripts/sync_bot.sh`.

```
bot/RiptideCards.cs    CardId-indexed value tables + per-deck combo factors
bot/RiptideEval.cs     leaf evaluation -> (0,1)
bot/RiptidePolicy.cs   move-ordering logits: search prior, rollout policy, panic fallback
bot/RiptideSearch.cs   determinized ensemble best-first search with max backup
bot/Riptide.cs         AI entry point: patron draft, turn clock, rule short-circuits
```

---

## 1. Architecture

### 1.1 Patron draft

Deliberate, not random (the 2025 champion drafts uniformly at random and threw
away all four picks). `Riptide.PatronPreference` is a static, tunable array:

```
ORGNUM > SAINT_ALESSIA > PELIN > HLAALU > RAJHIN > RED_EAGLE > ANSEI > PSIJIC > DUKE_OF_CROWS
```

Rationale:

* **Orgnum first** is the one point of agreement between the two 2025 bots that
  beat `SakkirinaSolo` head to head (`HQL_Plus_BOT`, `NAgent`). `BestMCTS3`
  actively refuses to draft it and rates the deck `A->C->C`, so drafting Orgnum
  puts it off-distribution.
* **Saint Alessia second** for the same reason from the other side: it is the
  newest deck, `BestMCTS3` refuses it outright, and `HQL_Plus_BOT` has almost no
  trained Q-values for it (`PELINAL_WHITESTRAKE` has data for 2 of 20 states,
  the `AYLEID_*` cards none at all). We price both decks properly.
* **Duke of Crows last**: three independent 2025 bots concluded that *holding*
  Crows favour is a liability (it permanently locks you out of the coin->power
  dump). We would rather leave it in the pool.

The draft is P1, P2, P2, P1 over a 6-patron pool, so **four** non-Treasury
patrons end up in play — the 4-favour instant win is genuinely reachable, and the
evaluation treats it as such (see §1.3). Out-of-pool patrons are listed in the
preference array so the draft stays legal and sane if the 2026 pool changes.

### 1.2 Search

**Determinized ensemble over complete turn plans.**

* `K = Search.Determinizations = 14` worlds per hard decision, each from
  `gameState.ToSeededGameState(seed)` with a *different* seed. The seed set is
  drawn once per turn and reused for every decision and every root move — common
  random numbers, so root-move comparisons are paired.
* Within a world: PUCT-guided best-first search with **max backup**. Every node
  inside a turn belongs to us (there is no opponent interleave until `END_TURN`),
  so max is the correct operator, not the mean.
* Unlike the 2025 champion's neutered `p = 0.9 + 0.1*prior`, the prior here
  genuinely steers selection: `score = q + c * prior * sqrt(N) / (1 + n_i)` with
  `c = 0.55` and a broad softmax temperature of 25 over the policy logits.
* Leaves are evaluated after `END_TURN` is applied, at the depth horizon
  (`MaxDepth = 24` plies), or at the node cap. Unexpanded children are estimated
  with a greedy policy rollout to end of turn.
* **Solved-subtree propagation.** Inside one determinization every transition is
  deterministic (seedless `ApplyMove` on a fixed-seed world), so a fully expanded
  subtree is genuinely solved. Solved children are removed from selection and the
  flag propagates upward; a world that solves its whole turn plan space drops out
  of the round robin and hands its budget to the others. Nobody in the 2025 field
  except `BestMCTS3` does this, and `BestMCTS3` only uses it to stop early.
* **Root aggregation**: mean of the per-world max-backup value for each root
  move, over the worlds that visited it, plus a tiny bonus for breadth of
  agreement. Every root move is swept once in every world before any world
  deepens, so no move carries a single-world estimate.

**Rule short-circuits are deliberately minimal** — only:

1. a single legal move, and
2. a `DESTROY_CARDS` / `DISCARD_CARDS` / `COMPLETE_TREASURY` choice whose card set
   is entirely `BEWILDERMENT`.

In particular **cards are never force-played outside the search**. That is the
2025 winner's biggest hole: its `resourceOnlyCards` whitelist contains the whole
Orgnum suite plus the Pelin combo payoffs, and it plays them in *shuffled list
order* — i.e. it randomises its own combo sequencing and the search never sees
those decisions.

Branching is capped (not filtered by hand-written rules): top 22 by policy score
in normal states, top 14 in choice states, with `END_TURN` always retained. This
exists purely to contain the `MAKE_CHOICE` combinatorial explosion
(`C(n,k)` summed over `k = Min..Max`).

### 1.3 Evaluation

`Eval.Evaluate(SeededGameState, PlayerEnum) -> (0,1)`, sigmoid-squashed at
`raw / 780`. One scale everywhere, no negation, no zero-sum mixing (the failure
that sank `MaltheMCTS`).

| Term | Notes |
|---|---|
| Card values | `double[125]` indexed by `(int)CardId`, continuous **0–100** rather than 5 buckets — far fewer ties than the field's tier lists. Orgnum and Alessia priced up, Gold at 6, Bewilderment at −30 plus an extra −22 penalty in deck. Flat-array lookup, no string scans. |
| Deck value | `sum / sqrt(max(1, n/16))` — dilution-aware. |
| Combo density | Real probability term: `P(X >= k)` for `X ~ Bin(5, c/n)` at k = 2,3,4, weighted `0.55/0.30/0.15` and by a per-deck combo payoff factor. The champion's equivalent is worth ~36 points against ~500 for one S-tier card, i.e. effectively nothing. |
| Prestige | Weight ramps `6 -> 40` per point with `progress = min(1, max(P)/40)`; `+1 prestige` treated as break-even (tempo correction). |
| 40-race | Explicit: `+900` when we are ≥40 and ahead, `−1400` when they are; graduated approach urgency inside 12 points of the threshold on *both* sides. The champion models the 76–79 window but has nothing at all for the far more decisive 33–40 stretch. |
| 80 danger zone | `−120` per point for the opponent in 76–79, `+70` for us. |
| Patron favour | Distance-to-sweep in patron calls (Ansei costs 1, everything else 2). **Both** defensive (`−2000 / −260 / −40` at distance 1/2/3) **and** offensive (`+900 / +160 / +30`). The champion's ratio is `−3000 : +50`, i.e. it will never race us to a patron win. |
| Crows | Holding Crows favour is penalised (decaying with game progress); the opponent holding it is a bonus. |
| Agents | Ours: `0.90 * cardValue + 7 * hp` (+12 taunt). Theirs: `−(1.05 * cardValue + 8 * hp + 15)`. No `2x + 40` overfear — the champion pays −1064 for an agent worth +512 to itself, which cheap disposable agents can bait. |
| Tavern denial | `−cardValue * (0.40 + 1.10 * opponentDeckAffinity)` per face-up card, so denial is targeted at what the *opponent's* deck actually wants. |
| Intra-turn | Unspent coins and unused patron calls are valued, so mid-turn horizon states are not blind (the champion's eval has literally zero resolution on coins and power). |

Mid-turn, unspent `Power` is folded into effective prestige minus whatever the
opponent's taunt agents will soak up at end of turn.

### 1.4 Time management

The 10 s is **cumulative across every `Play()` call in one turn** and an overrun
is an instant loss, so:

* `Stopwatch` restarted at the **top** of `Play()`, so determinization, prior
  computation, trimming and the fallback path are all inside the measured budget.
* Turn boundary detected by `remainingTime` jumping back up (authoritative,
  engine-reported), with `_turnUsedSeconds` accumulated independently.
* Budget = `min(remainingTime − 0.70, 9.3 − turnUsed) − elapsedThisCall`.
  Both limits apply; the tighter wins.
* Per-decision slice = `min(0.80, budget / 4)`.
* Below `0.30 s` remaining: **greedy policy pick, no search** (a policy fallback,
  not `BestMCTS3`'s uniformly random one).
* The clock is checked at every node in the tree loop and at every step inside a
  rollout.
* Everything in `Play()` is wrapped in a try/catch that falls back to the greedy
  policy, logs the exception type, and counts it — an unhandled exception is
  exactly as fatal as a timeout.

### 1.5 Memory

* No cross-turn or cross-call tree reuse in v1: the tree is built and discarded
  inside a single `Play()` call.
* `MaxNodesPerDecision = 6000` retained `PlanNode`s; past the cap the search
  degrades to rollouts instead of expansion.
* Rollouts retain nothing.
* No transposition table (so nothing to leak — `MaltheMCTS`'s 256 MB failure mode
  is structurally impossible here).

### 1.6 Per-game state

`GameEnd` and `PregamePrepare` both call `Reset()`, which clears the clock, the
seat id, the seeds and the error counters. The RNG is reseeded per game from
`DateTime.UtcNow.Ticks`, `Environment.TickCount` and a fresh `Guid` — the AI
instance is reused across every game in a run, so a fixed constant seed (what
every 2025 bot uses) would make us fully reproducible and offline-exploitable.

---

## 2. Benchmarks

Competition conditions: `-to 10` (10 s/turn), mirror pairs (same seed, seats
swapped) via `scripts/bench.py`.

| Opponent | Result | Notes |
|---|---|---|
| `RandomBot` | 2/2 (100%) | smoke test |
| `MCTSBot` (built-in) | **12/12 (100%)** | 3 mirror pairs each on seeds 200–202 and 900–902; 3/3 in every seat |
| `BestMCTS3` (Archive2025, 4th in 2025 at 66.1%) | **7/16 (43.8%)**, 95% CI 23–67% | 8 mirror pairs, seeds 700–707; 4/8 as P1, 3/8 as P2 |
| `BestMCTS3`, fixed-seat spot check | 3/4 as P1 | `-n 4 -s 77 -to 10`, all four ended on Prestige>40 |

**v1 does not beat `BestMCTS3`.** 43.8% over 16 games is far too few to
distinguish from anything between 25% and 65%, but the honest read is "roughly
competitive, probably still behind".

Measured progression on the same paired seeds (700–707) during development:

| Change | Wins / 16 |
|---|---|
| K=6 determinizations, `DeckWeight` implicitly 1.0 | 5 (31.2%) |
| K=14 determinizations | 5 (31.2%) |
| K=14 + `DeckWeight = 1.8` | 7 (43.8%) |

An earlier 10-game run (seeds 500–504) showed 0/5 as P2; the seat-balanced
16-game run shows that was seed correlation across two runs that reused the same
five seeds, not a seat bug.

**Timing and safety.** `Ends due to other factors: 0` in every batch — zero
`TURN_TIMEOUT`, zero `BOT_EXCEPTION`, zero `INCORRECT_MOVE`, zero recovered
exceptions, zero panic fallbacks logged.

Instrumented turn statistics (`RIPTIDE_DIAG=1`, writes one line per turn to
stderr), vs `BestMCTS3` at `-to 10`:

| | K=6 | K=14 |
|---|---|---|
| Turn wall time used (of a 9.3 s ceiling) | 0.31 – 2.22 s | 0.31 – 5.80 s |
| Searched decisions per turn | 6 – 11 | 8 – 9 |
| Search iterations per turn | 35 k – 104 k | 122 k – 228 k |
| Retained nodes per turn (summed over decisions) | 12 k – 42 k | 49 k – 89 k |

The important finding: at K=6 the search **solved the entire turn plan space of
every world** and exited early, leaving 7–9 s of the budget unused. Extra depth
was not available to buy, so the spare budget went into more hidden-information
samples (K=6 → 14). Even at K=14 the worst turn observed used 5.8 s of 9.3 s, so
there is still headroom and a ~4 s margin to the hard limit.

---

## 3. Known limitations (v1)

0. **The search saturates; the evaluation is now the whole bottleneck.** Each
   world's turn plan space is solved exactly, so within a turn there is nothing
   left for more search to find. Whatever the eval mis-scores, the search
   optimises *harder* against — the exact mechanism that put `MaltheMCTS` last.
1. **Strategy fusion.** Determinizations are solved independently and the root
   value is the mean of per-world optima, which implicitly assumes we can adapt
   perfectly to each world. Full solving makes this optimism *worse* than it
   would be under a shallow search. A consensus/vote aggregate, or penalising
   moves whose per-world values disagree, would be more honest.
2. **No opponent model at all.** The search stops at `END_TURN`; nothing models
   what the opponent does with the position we hand them. (`SakkirinaSolo` beat
   `Sakkirina` by *deleting* its opponent lookahead, so this is a deliberate v1
   choice, not an oversight — but a *correct* antisymmetric one would help.)
3. **No belief update.** Determinizations are drawn from the engine's uniform
   re-split of the opponent's hand+draw. Observed buys and plays are not used to
   condition the sample.
4. **No tree reuse.** Every `Play()` call rebuilds `K` trees from scratch, which
   wastes most of the work done by the previous call inside the same turn.
5. **No transposition table.** Order-independent card plays are searched as
   distinct subtrees, which is exactly the branch of the space combo sequencing
   lives in.
6. **Card values are hand-written**, not fitted. So are all evaluation weights.
   `BestMCTS3`'s optimiser moved its hand-tuned weights a long way
   (Prestige 10/60/200 -> 14.4/115.4/244.1), which suggests real headroom.
7. **Rollouts are greedy and deterministic**, so the value of a position is
   "what my greedy policy achieves from here" — every line requiring non-greedy
   play (banking coins for a bigger buy, deliberate tempo sacrifice) is
   systematically undervalued.
8. **Choice branching is capped at 14** by policy score; a genuinely good but
   low-prior choice combination can be pruned.
9. **The patron preference order is untested.** It is reasoned from the 2025
   analyses, not measured.

---

## 4. Tuning knobs

All are `const` / `static readonly` and grouped at the top of their file.

**`Search`**

| Knob | Default | Effect |
|---|---|---|
| `Determinizations` | 14 | worlds per decision; more = better hidden-info averaging, less depth each |
| `PriorTemperature` | 25 | lower = policy dominates selection |
| `Exploration` | 0.55 | PUCT `c` |
| `MaxDepth` | 24 | plies within a turn before truncating to a rollout |
| `MaxRolloutLength` | 70 | hard stop on runaway rollouts |
| `MaxNodesPerDecision` | 16000 | memory cap |
| `MaxRootBranch` / `MaxChoiceBranch` | 22 / 14 | branching caps |

**`Riptide`**

| Knob | Default | Effect |
|---|---|---|
| `PatronPreference` | array | draft order |
| `TurnCeilingSeconds` | 9.3 | self-imposed cumulative turn ceiling |
| `TurnSafetyMargin` | 0.70 | subtracted from engine-reported remaining |
| `MaxSliceSeconds` / `SliceDivisor` | 0.80 / 4 | per-decision slice |
| `PanicThresholdSeconds` | 0.30 | below this: greedy, no search |

**`Eval`** — `DeckWeight` (deck quality vs prestige; **the highest-leverage
single knob** — moving it 1.0 -> 1.8 was worth +2 wins over 16 paired games),
`Scale`, `PrestigeBase`, `PrestigeSlope`, `TempoOffset`,
`Lead40Bonus`, `Behind40Penalty`, `ApproachStep`, `OwnApproachStep`,
`Danger80Step`, `Own80Step`, `PatronDiff`, `EnemyPatronDist[]`,
`OwnPatronDist[]`, `CrowsHoldPenalty`, `AnseiFavourBonus`, `DeckNorm`,
`ComboWeight`, `BewildermentExtra`, the agent weights, `TavernDeny*`,
`CoinValue`, `PatronCallValue`, `UpcomingDrawValue`.

**`CardValues`** — `Deck[]` (value in deck), `InHand[]` (value in hand right
now), `ComboFactor[]` (per-deck combo payoff density).

---

## 5. Build and run

```bash
scripts/sync_bot.sh                      # copy bot/*.cs -> engine, build, stage DLL

cd ScriptsOfTribute-Core/GameRunner
DOTNET_ROLL_FORWARD=LatestMajor dotnet bin/Release/net8.0/GameRunner.dll \
    Riptide BestMCTS3 -n 4 -s 77 -to 10

python3 scripts/bench.py Riptide BestMCTS3 --pairs 10 --seed 500 --workers 4
```

Per-turn diagnostics (time used, decisions searched, iterations, retained nodes,
panic fallbacks) go to **stderr**, one line per turn, when `RIPTIDE_DIAG=1` is
set. Off by default and off the hot path.

```bash
RIPTIDE_DIAG=1 DOTNET_ROLL_FORWARD=LatestMajor \
  dotnet bin/Release/net8.0/GameRunner.dll Riptide BestMCTS3 -n 1 -s 500 -to 10 \
  2> /tmp/riptide_diag.txt
sort -t= -k2 -rn /tmp/riptide_diag.txt | head   # worst turns first
```

Note `bench.py` must be run from the repository root (it resolves the runner
path relative to itself but inherits the caller's working directory for the
`python3` invocation).

`DOTNET_ROLL_FORWARD=LatestMajor` is required — only the .NET 10 runtime is
installed and `GameRunner` targets net8.0.
