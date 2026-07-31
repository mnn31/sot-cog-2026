# Riptide v2 — tunable evaluation

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
