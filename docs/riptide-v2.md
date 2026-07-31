# Riptide v2 — tunable evaluation, hardware-adaptive clock

v1's verdict was that the search saturates: inside a turn every determinization's
plan space is solved exactly, so more search buys nothing and **the evaluation
function is the entire bottleneck** (v1 §3.0). v2 does not change the search. It
makes the evaluation *externally tunable* and runs a first automated tuning pass
against `BestMCTS3`.

Read `docs/riptide-v1.md` first — everything there still describes the bot except
where noted below.

---

## 1. What changed in the bot

### 1.1 `bot/RiptideWeights.cs` (new)

A static weight table. Every tunable number in `Eval` and `CardValues` is now
read through

```csharp
public static double G(string key, double def)
```

which returns the compiled-in default unless an override is present in the file
named by the **`RIPTIDE_WEIGHTS`** environment variable (absolute path). A tuning
candidate is therefore a file, and a tuning iteration costs zero rebuilds.

* **Format** — a flat JSON object of `"key": number` pairs. It is valid JSON, so
  the tuner emits it with `json.dump` and it diffs cleanly, but it is read by a
  ~40-line hand-rolled parser in the bot. The `Bots` project is `netstandard2.1`
  and references nothing but the engine; `System.Text.Json` is **not** available
  there without adding a package, and adding a package to a competition entry to
  read 38 numbers is not a trade worth making.
* **Load-once semantics** — the parse happens in the CLR static constructor of
  `Weights`, so it runs exactly once per process no matter how many `Riptide`
  instances GameRunner builds. `Riptide`'s constructor calls
  `Weights.EnsureLoaded()`, which additionally forces `Eval` and `CardValues` to
  initialise so a malformed file fails at construction time rather than silently
  reverting to defaults halfway through a tournament.
* **Audit line** — when `RIPTIDE_WEIGHTS` is set the bot prints one line to
  **stderr** (never stdout, which is where GameRunner's result lines are parsed
  from):

  ```
  [riptide] weights '/abs/path.json' overrides=38 DeckWeight=1.8 Scale=780
  ```

  It prints the *live field values*, not the file contents, so the line is proof
  the overrides actually reached the evaluation. A failed load prints
  `[riptide] WEIGHTS LOAD FAILED …` instead, and `scripts/tune.py` treats both a
  failed load and a **missing** audit line as fatal — a tuner that silently
  measures the default weights twice is worse than one that crashes.

### 1.2 Key space

| Key pattern | Covers |
|---|---|
| `eval.<FieldName>` | the 38 scalar evaluation weights |
| `eval.EnemyPatronDist1..3`, `eval.OwnPatronDist1..3` | entries of the two patron-distance arrays |
| `card.<CARD_ID>.deck`, `card.<CARD_ID>.hand` | the 125-entry card value arrays, by enum name |
| `card.<index>.deck`, `card.<index>.hand` | the same, by raw `(int)CardId` (applied second, so it wins) |
| `combo.<PATRON_ID>` / `combo.<index>` | the 16-entry per-deck combo payoff factors |

### 1.3 `const` → `static readonly`

The `Eval` knobs were `const double`. They are now `static readonly double`
initialised from `Weights.G(...)`, with the old literal as the default argument.
The JIT still folds a `static readonly double` after class init, so the hot path
is unchanged — and the search's own knobs (`Search.Determinizations`,
`PriorTemperature`, …) are deliberately left as `const`, because v1 established
that the search is not the constraint.

### 1.4 Three constants promoted to knobs

While externalising, three numbers that were buried as literals became first-class
weights (they were tunable in principle and there was no reason to hide them):

| New key | Was | Meaning |
|---|---|---|
| `eval.ComboGe2` / `Ge3` / `Ge4` | `0.55` / `0.30` / `0.15` | mixture over `P(X>=k)` in the combo-density term |
| `eval.EnemyCoinValue` | `0.5` | discount on the opponent's banked coins off-turn |

That brings the scalar count to **38**.

### 1.5 Verification that the file actually drives play

Two checks, both required — the first proves the plumbing, the second proves the
plumbing *matters*:

1. The audit line reports the overridden value (`DeckWeight=0.05` from a file
   setting `eval.DeckWeight: 0.05`), i.e. the live field, not the literal.
2. A file that **negates all 125 card values** and cranks `eval.DeckWeight` to
   6.0 — a bot that deliberately buys the worst cards available — goes
   **0/2 vs `MCTSBot`** on seeds 200–201, where v1 is documented 12/12. The same
   binary with no `RIPTIDE_WEIGHTS` set is unaffected.

---

## 2. `scripts/tune.py` — the tuning loop

SPSA (Spall 1992). The choice is forced by the cost structure: one objective
evaluation is ~32 games of Tales of Tribute (~5 minutes on 10 cores), and SPSA
needs exactly **two** evaluations per iteration regardless of dimension, where a
coordinate-wise finite difference would need 76.

**Parameterisation.** `x_i = log(w_i / default_i)`, starting at `x = 0`. Log
space keeps every weight positive — all 38 defaults are positive and a sign flip
would be meaningless for all of them — and makes one perturbation size `c`
meaningful across weights spanning `0.15` to `2000`. It also matches how
`BestMCTS3`'s own optimiser moved its weights (multiplicatively: Prestige
`10/60/200 -> 14.4/115.4/244.1`).

**One iteration.**

```
delta ~ Rademacher(+/-1)^38
w+ = default * exp(x + c*delta)        w- = default * exp(x - c*delta)
y+, y- = win rate vs BestMCTS3 over the SAME 16 mirror pairs (32 games)
ghat_i = (y+ - y-) / (2*c*delta_i)
x     <- clip(x + a*ghat)                                   (ascent)
```

* **Common random numbers.** `y+` and `y-` use the identical seed set, and every
  seed is played in both seats. The deal-luck component of the difference
  cancels; only the weight difference and the two bots' internal randomness
  remain.
* **Fresh seeds every iteration** (`seed + k*pairs`), so the trajectory is not
  fitted to one seed set. Pairing is only needed *within* an iteration.
* **Gains.** `c = 0.45` (each weight probed at ×1.57 and ÷1.57), `a = 0.75`,
  per-parameter step clipped to `±0.35` in log space and `|x|` clipped to `1.20`
  (≈ ×3.3 / ÷3.3 from the default). `c` is deliberately large: the objective is
  noise-limited, and a bigger perturbation trades finite-difference bias for
  signal-to-noise, which is the right trade here.
* **Deployment iterate.** SPSA iterates are noisy; the final answer is a
  Polyak-style tail average of the last 3 iterates, not the single last one.
* **Persistence.** Every iteration appends a JSON record to
  `results/tuning-log.jsonl` — `x`, `delta`, both weight vectors' results split
  by seat, `ghat`, `x_next`, `theta_next`, wall time. A run resumes from the last
  record (replaying the perturbation RNG so directions are not repeated), so the
  loop is interruptible and every number in this document is auditable.
* **Contamination guard.** Any game ending on "other factors"
  (`TURN_TIMEOUT` / `BOT_EXCEPTION` / `INCORRECT_MOVE`) is counted and reported;
  v1 measured zero of these and any nonzero count invalidates a batch.

**Subcommands.**

```bash
scripts/tune.py check                      # DEFAULTS here vs the C# literals
scripts/tune.py dump  --out weights/baseline.json
scripts/tune.py run   --iters 14 --pairs 16 --workers 10 --budget-min 80
scripts/tune.py report                     # trajectory + noise floor
scripts/tune.py validate --candidates default results/tuned-final.json \
                         --pairs 16 --seed 9000 --workers 10
```

`check` parses `bot/RiptideEval.cs` and asserts the tuner's default table still
equals the compiled-in literals — the one failure mode that would make every
subsequent number meaningless.

---

## 3. Hardware-adaptive turn clock

Supersedes v1 §1.4 and the `MaxSliceSeconds` / `SliceDivisor` rows of v1 §4.

### 3.1 What was wrong

v1 sized its per-decision slice with a constant: `min(0.80 s, left / 4)`. The
0.80 was measured on a fast Mac. Evidence that it does not travel: on the Mac
Riptide beats `BestMCTS3` ~52% (n = 48); on a slower, contended machine, **same
seeds**, it fell to 37.5% while `BestMCTS3` — which probes its own iteration rate
each turn and resizes its budget from it — held up.

One constant, two separate defects:

1. **No hardware term.** 0.80 s is roughly what a decision needs to solve on this
   Mac. On a machine a third as fast it is a third of a solve, so the search
   returns a truncated, partly single-world root aggregate. The bot does not get
   *slightly* worse, it gets qualitatively worse, because the whole design
   (v1 §1.2) rests on solving each determinization's plan space exactly.
2. **No redistribution.** The cap applied per decision regardless of what the
   rest of the turn was doing. v1's own diagnostics: 30 turns vs `BestMCTS3`
   averaged **3.61 s** of a 9.30 s ceiling and peaked at **5.85 s** — up to 6 s a
   turn sitting idle while individual decisions were being cut off at 0.80 s.
   Decisions that solved in 40 ms had no way to hand their unused share to the
   decisions that did not solve at all.

### 3.2 The scheme

Everything below lives in `bot/Riptide.cs`; `Search` gained one output,
`Converged`, and nothing else changed in the search.

The slice for one decision is

```
remaining = DecisionCountSafety * decisionsPerTurnEWMA + DecisionCountPad - decisionsDoneThisTurn
slice     = SliceBoost * (left - PanicThreshold) / remaining
slice     = min(slice, MaxSliceSeconds)
slice     = max(slice, solveFloor, sweepFloor, MinSliceSeconds)   # two measured floors
slice     = min(slice, MaxSliceFraction * left, left - PanicThreshold/2)
```

with `left = min(engineRemaining - TurnSafetyMargin - guard,
turnCeiling - guard - turnUsed) - elapsedThisCall`.

| Input | How it is measured | What it does |
|---|---|---|
| `decisionsPerTurn` | EWMA (α = 0.25) of *searched* decisions per turn, folded in at `END_TURN`. Mac: 7–13, mean ≈ 9.3 | denominator of the share. Inflated by ×1.15 + 0.5 on purpose — under-estimating starves the end of the turn, which is where the buys are |
| `left` | recomputed from the engine's own `remainingTime` on **every** call | the redistribution mechanism. A decision that converges in 40 ms leaves `left` untouched, so the next decisions' shares grow. This is what v1 had no way to express |
| `solveSeconds` | EWMA (α = 0.30) of wall time over decisions that **converged**, i.e. solved every determinization. Never updated from a truncated search | floor A. If a solve costs 1.5 s on this machine, a 0.9 s slice reliably buys a truncated search — spend the 1.5 s. Capped at 25% of `left` so it decays with the turn instead of cliff-edging the tail |
| `iterationsPerSecond` | EWMA (α = 0.25) of `Search.Iterations / elapsed`, live, per decision. Mac: 48–81 k/s | floor B: a slice must afford `SweepsPerWorld × K × branch` iterations, i.e. several full root sweeps in every determinization. Below that most root moves carry a single-world estimate and the aggregate is worse than the greedy policy. Grows in exact proportion to how slow the machine is |
| `guard` | decayed max of `2.5 ×` the observed overrun past our own deadline | subtracted from the ceiling *and* from the engine limit. This is the cost of the longest uninterruptible unit of work (one `ApplyMove`), which is exactly what would eat the margin on a stalled machine |

`SliceBoost = 1.5` is the largest value that still covers 11–13 searched
decisions in a turn without the tail falling into the greedy fallback, worked
through against the measured decisions-per-turn distribution: with `boost = 2.0`
a fully-unconverged turn runs out of budget at decision 9.

**`MaxSliceFraction = 0.25` is the one piece of v1's rule that was worth
keeping**, and leaving it out was a real bug in the first cut of this change. A
share-of-an-estimated-count rule spends a *linear* fraction of the budget per
decision, so if the count is under-estimated the turn simply runs out. A
fixed-fraction cap spends a *geometric* fraction, so what is left can approach
zero but never reach it — the turn can absorb any number of decisions. It only
binds late in a turn, once the share term has grown past a quarter of what is
left. Measured cost of omitting it: six times oversubscribed, the first cut spent
9.03 s of a 9.25 s budget and dropped up to 12 tail decisions per game into the
greedy fallback, where v1 dropped none (§3.4).

**Why a generous slice is free.** `Search.Run` already exited its round-robin the
moment every determinization's plan space was solved (v1 §1.2, solved-subtree
propagation); it now *reports* that as `Converged`. So the two halves of the
stated design goal are handled by different mechanisms and do not fight: the
search stops early when it has solved the turn space, and the slice is what lets
it keep going when it has not. Widening the cap cannot waste time it does not
need.

**Calibration survives `GameEnd`.** `Reset()` clears every game- and turn-scoped
field as before, and deliberately keeps the four calibration values — they
describe the machine, not the game, and `guard` in particular is a safety margin
that should not be re-learned from scratch in every game. All four are clamped
at the point of use, and only `guard` enters the ceiling computation (where it
can only tighten it), so a stale value can cost search quality but not a timeout.

**Knobs** (all `const` in `bot/Riptide.cs`, replacing v1's `MaxSliceSeconds`
`0.80` / `SliceDivisor` `4`):

| Knob | Default | Effect |
|---|---|---|
| `SliceBoost` | 1.50 | share multiplier; higher = more front-loaded |
| `MaxSliceFraction` | 0.25 | tail guarantee; the geometric decay rate of `left` |
| `MaxSliceSeconds` / `MinSliceSeconds` | 2.50 / 0.12 | absolute per-decision bounds |
| `DecisionCountSafety` / `DecisionCountPad` | 1.15 / 0.50 | how much the decisions-per-turn estimate is inflated |
| `DecisionsEwmaAlpha` / `SolveEwmaAlpha` / `RateEwmaAlpha` | 0.25 / 0.30 / 0.25 | smoothing of the three measurements |
| `SolveFloorFactor` / `SolveFloorFraction` | 1.00 / 0.25 | floor A strength and its cap |
| `SweepsPerWorld` | 4.0 | floor B: root sweeps per determinization a slice must afford |
| `GuardMultiplier` / `GuardDecay` / `MinTimeGuard` / `MaxTimeGuard` | 2.50 / 0.97 / 0.05 / 1.20 | learned overrun guard |
| `DefaultDecisionsPerTurn` / `DefaultSolveSeconds` / `DefaultIterationsPerSecond` | 9.0 / 0.25 / 35 000 | cold-start values, used until the first turn has been measured |

`TurnCeilingSeconds` (9.3), `TurnSafetyMargin` (0.70) and
`PanicThresholdSeconds` (0.30) are unchanged from v1.

### 3.3 Safety

Five independent bounds on the same quantity, all still in force:

1. `engineRemaining - 0.70 - guard`, using the engine's **authoritative**
   per-call report. This alone means a timeout requires one single `Play` call to
   overrun its own deadline by more than 0.75 s — our cumulative accounting
   could be wrong and this bound would still hold.
2. the 9.3 s cumulative self-ceiling, minus `guard`, minus `turnUsed`.
3. `MaxSliceSeconds = 2.5` per decision.
4. `MaxSliceFraction = 0.25` of what is left, which bounds the *tail* rather than
   any single slice.
5. `left - PanicThreshold/2`, applied last and unconditionally, so the greedy
   escape hatch below 0.30 s always survives.

The `Stopwatch` still starts at the top of `Play()` (all determinization, prior
and fallback overhead counted), the clock is still polled at every node and every
rollout step, and the try/catch greedy fallback is unchanged.

New: `GameEnd` writes one **stderr** line if the game ended on `TURN_TIMEOUT`,
`BOT_EXCEPTION`, `INCORRECT_MOVE`, `PATRON_SELECTION_TIMEOUT` or
`PREPARE_TIME_EXCEEDED` *and we were the loser*. GameRunner only reports these as
an aggregate "ends due to other factors" and never says whose fault it was, which
made a timing regression invisible in a batch result. `scripts/bench.py` now
surfaces both — the aggregate count per matchup, and any `[riptide] FATAL` line —
so "the batch had 12 fatal ends but none of them were ours" is a statement the
harness can actually make. It turned out to matter (§3.4).

### 3.4 Verification

All on the 11-core Mac, `-to 10`, mirror pairs via `scripts/bench.py`. "Before"
is a frozen snapshot of the v1 build
(`RIPTIDE_RUNNER=/tmp/riptide_frozen_v1/GameRunner.dll`), so both arms can be
re-run at will without a rebuild contaminating anything.

> **Read the win rates with the noise floor in mind.** Reusing a seed does *not*
> make two runs comparable game-for-game: `Reset()` reseeds Riptide's RNG per
> game from the wall clock (v1 §1.6, deliberate — a fixed seed would make the bot
> offline-exploitable), and a deadline-driven search is non-deterministic anyway.
> A seed fixes the *deal*, nothing else. Measured directly: the **same shipped
> binary on the same 12 pairs** scored 14/24 and 8/24 on two separate runs. At
> n = 24 the standard error is ~10 points, so anything under a ~20-point gap at
> that sample size is unreadable. The timing measurements below (budget use,
> overruns, fallbacks, fatal ends) are direct and stable; the win rates are not,
> and are pooled and reported with their intervals rather than cherry-picked.

**Budget use** — `RIPTIDE_DIAG=1`, 2 games vs `BestMCTS3`, seed 3000:

| | v1 (fixed slice) | v2, first cut | **v2 (shipped)** |
|---|---|---|---|
| Mean turn wall time (of 9.3 s) | 3.61 s | 4.79 s | **4.18 s** |
| Worst turn | 5.85 s | 9.03 s | **8.37 s** |
| Mean searched decisions / turn | 8.6 | 9.3 | 9.2 |
| Mean search iterations / turn | 192 k | 224 k | **215 k** |
| Max overrun past our own deadline | — | 0.015 s | **0.010 s** |
| Learned `guard` | — | 0.05 s floor | **0.05 s floor** |
| Greedy fallbacks | 0 | 0 | **0** |
| `[riptide] FATAL` | n/a | 0 | **0** |

The peak turn moved from 63% to 90% of the self-imposed ceiling and mean search
work per turn rose ~12%, with the extra time landing where it should: turns
carrying unconverged decisions (`unconv` in the diag line) are the ones that now
run long, while turns that solve everything still finish in 2–3 s. The shipped
build sits slightly below the first cut on every utilisation row — that is
`MaxSliceFraction` holding budget back for the tail, and it is the trade the
oversubscription table below justifies.

**Strength, full speed.** Every full-speed batch run, nothing dropped:

| Build | Batch | Wins | Rate | Other-factor ends |
|---|---|---|---|---|
| v1 (fixed slice) | 12 pairs, seeds 3000–3011, 4 workers | 12/24 | 50.0% | 0 |
| v1 | 24 pairs, seeds 3020–3043, 5 workers | 24/48 | 50.0% | 0 |
| v2, first cut | 12 pairs, seeds 3000–3011, 4 workers | 13/24 | 54.2% | 0 |
| v2 (shipped) | 12 pairs, seeds 3000–3011, 4 workers | 14/24 | 58.3% | 0 |
| v2 (shipped) | 12 pairs, seeds 3000–3011, 4 workers, **repeat** | 8/24 | 33.3% | 0 |
| v2 (shipped) | 24 pairs, seeds 3020–3043, 5 workers | 19/48 | 39.6% | 0 |

Pooled, shipped build against the frozen v1:

| | Pooled | Rate | 95% CI |
|---|---|---|---|
| v1 | 36/72 | 50.0% | 38.7–61.3% |
| v2 (shipped) | 41/96 | 42.7% | 33.3–52.7% |

Two-proportion test: **z = 0.94, p = 0.35** — not a detectable difference. But the
point estimate is 7.3 points *below* v1, not above it, and that has to be stated
plainly rather than buried: **this change is not demonstrated to be neutral at
full speed, only "not distinguishable" at the sample size that was affordable.**
Detecting a gap this size at 80% power needs ~730 games per arm; the whole
comparison here is 168 games. The two identical-seed runs of the identical
shipped binary (58.3% and 33.3%) are the scale of the noise.

There is a mechanism that would make a real regression plausible, and it is
already written down in v1 §3.0: inside a turn the search saturates, so the
evaluation is the bottleneck, and *more* search means optimising harder against
whatever the hand-written eval mis-scores. Spending 4.18 s a turn instead of
3.61 s buys exactly that on hardware where v1 was already converging. If a future
higher-powered run confirms a full-speed cost, the knob to turn is `SliceBoost`
toward 1.0, which recovers roughly v1's fast-hardware behaviour while keeping the
floors, the redistribution and the tail guarantee for slow hardware — the parts
that address the actual reported failure. It is left at 1.5 because the brief was
explicitly to search longer when the turn space is unsolved and budget remains.

**Contention** — 8 background `yes` processes plus `--workers 10` on 11 cores
(≈ 18 CPU-bound processes), 8 mirror pairs, seeds 3000–3007:

| | Wins | Rate | Other-factor ends |
|---|---|---|---|
| v1 | 10/16 | 62.5% | **1** |
| v2, first cut | 10/16 | 62.5% | 0 |
| **v2 (shipped)** | 8/16 | 50.0% | **0** |

The only row worth reading here is the last column: v1 produced a contaminated
game (a fatal end that GameRunner could not attribute), the new builds produce
none. At n = 16 the win-rate column spans a 95% CI of roughly 28–72% and carries
no information.

**This benchmark did not reproduce the pod regression.** Under contention on this
Mac, v1 scored *above* its own full-speed result, so the 52% → 37.5% collapse is
not a contention effect this machine can produce. That is an honest negative: the
change is justified by the mechanism and by the timing measurements, not by a
reproduced win-rate recovery.

**Heavy oversubscription (the real slow-hardware proxy)** — 16 background `yes`
processes plus `--workers 10` on 11 cores, ≈ 26 CPU-bound processes for 11 cores.
This is the only local setup that actually reproduces slow hardware: the measured
iteration rate drops from 48–81 k/s to 16–35 k/s (2–5×), and almost nothing
converges (`unconv` is 7–11 of 9–13 searched decisions per turn, versus 0 on most
full-speed turns). Diagnostics, 2 games, seed 3100:

| | v1 | v2, first cut | **v2 (shipped)** |
|---|---|---|---|
| Worst turn (of 9.3 s) | 8.46 s | 9.03 s | **8.57 s** |
| Max search iterations / turn | 147 k | 163 k | **175 k** |
| Greedy fallbacks (per game, worst) | 0 | **12** | **0** |
| Max overrun past our own deadline | — | 0.070 s | **0.030 s** |
| Learned `guard` (from 0.05 s floor) | — | rose to 0.13 s | 0.05–0.06 s |
| `TURN_TIMEOUT` / `BOT_EXCEPTION` | 0 | 0 | **0** |

The middle column is why `MaxSliceFraction` exists. Without it the scheme spent
the turn budget *too* eagerly and paid for it at the end of the turn, which is
the worst possible place to pay. With it, the budget is still used (8.57 s worst
turn, more iterations than either alternative) and the tail is intact.

Win rate on this configuration is 6/12 for the shipped build and 9/12 for the
first cut — 95% CIs of 25–75% and 47–91%. Twelve games says nothing; both are
reported only so neither is quietly omitted.

**Timeout safety under a 3× harsher clock** — 12 mirror pairs at `-to 4`, which
cuts Riptide's whole turn budget to 3.25 s:

| | Wins | Other-factor ends | Riptide-attributed (`[riptide] FATAL`) |
|---|---|---|---|
| v1 | 19/24 | 12 | n/a (no self-report in v1) |
| **v2** | **19/24** | 12 | **0** |

Twelve of twenty-four games ended fatally — and the new self-report shows **none
of them were ours**: at a 4 s turn clock `BestMCTS3` times out, Riptide does not.
This also disqualifies `-to N` as a slow-hardware simulation, since it breaks the
opponent rather than us, but it is a strong safety result on its own: 24 games at
a third of the normal budget, zero Riptide timeouts, zero exceptions.

### 3.5 Residual risks

* **Full-speed strength is unresolved, and the point estimate is down.** 42.7%
  (41/96) against v1's 50.0% (36/72), p = 0.35. Not significant, but not the
  ">= similar" the change was supposed to deliver either, and v1 §3.0 supplies a
  mechanism by which more search against a hand-written eval genuinely hurts.
  This is the single biggest open question about the change. Resolving it needs
  ~730 games per arm; the fallback if it is real is `SliceBoost -> 1.0`.
* **The pod regression is still unreproduced locally.** The mechanism (truncated,
  unconverged searches plus 6 idle seconds a turn) is measured and fixed; that it
  was *the* cause of 52% → 37.5% is inference. Re-run the pod comparison when the
  calibration job releases it — that is also the only environment where the
  change's benefit can actually be demonstrated rather than argued.
* **`guard` is reactive.** It protects against a *repeated* long uninterruptible
  `ApplyMove`, not the first one. A single 0.8 s stall on the very first decision
  of a game would still eat the 0.70 s margin. This is inherited from v1, not
  introduced here; the fixed margin remains the only defence against a first
  occurrence.
* **Higher ceiling utilisation.** Peak turn use went from 63% to 90% of the 9.3 s
  self-ceiling, i.e. ~8.4 s of the engine's 10 s. Every bound is unchanged, but
  the bot now routinely operates near them instead of far below, so anything that
  makes a single `ApplyMove` pathologically slow has less room than before. This
  is the deliberate trade the change makes, and it is the thing to watch first if
  a timeout ever appears in a tournament log.
* **This bot's win rate cannot be measured at n = 24 on this harness.** The same
  binary scored 14/24 and 8/24 on identical seeds. Any future comparison of two
  Riptide builds needs n in the hundreds, or a variance-reduction change (a
  fixed RNG seed behind a benchmark-only env var) that does not ship.
* **`decisionsPerTurn` is a single EWMA** over a 7–15 spread. A 15-decision turn
  following a run of 7-decision turns front-loads and pushes the tail into
  shorter slices. This is bounded rather than eliminated: `MaxSliceFraction`
  guarantees the tail cannot be starved outright, and no greedy fallback was
  observed in the shipped build in any run above — but the tail's slices are
  still smaller than they should be on such a turn.
* **`-to 4` breaks `BestMCTS3`, so it cannot be used to measure strength on a
  short clock.** Half those games end on the opponent's timeout. Any future
  short-clock experiment needs an opponent that survives it.
* **Slice knobs are unfitted.** `SliceBoost`, `DecisionCountSafety`,
  `SolveFloorFraction` and `SweepsPerWorld` are reasoned from the measured
  distributions, not tuned. They are `const` and outside the `RIPTIDE_WEIGHTS`
  key space (§1.2), so `scripts/tune.py` will not touch them.
