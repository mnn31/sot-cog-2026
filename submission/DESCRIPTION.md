# Primal Kyogre — Technique Description

**IEEE CoG 2026 Tales of Tribute AI Competition**

- **Agent name:** Primal Kyogre (class name in the submitted source: `PrimalKyogre`)
- **Author / affiliation:** Manan Gupta, The Harker School
- **Submission:** single C# file (`PrimalKyogre.cs`), compiles into the
  `ScriptsOfTribute-Core` `Bots` project (netstandard2.1, LangVersion 10) with no
  added dependencies
- **Language / runtime:** C# 10, .NET Standard 2.1, no external packages
- **Configuration submitted:** every number the agent plays with is compiled into
  the submitted file. It reads no configuration file, no environment variable and
  no learned artefact at runtime, so the file below *is* the configuration that
  was benchmarked.

---

## 1. Overview

The agent treats a Tales of Tribute turn as a **single-agent planning problem
under hidden information**, not as a two-player game tree. Within one turn the
opponent never acts, so every state transition after `ApplyMove` is
deterministic once the hidden information is fixed. The agent exploits that
directly: it samples an ensemble of determinized worlds, searches each one for
the best *complete turn plan*, and combines the per-world verdicts into one root
decision.

The four components are:

1. a **determinized-ensemble turn-plan search** (Section 2),
2. an **adaptive time manager** that sizes each decision's slice from measured
   hardware throughput (Section 3),
3. a **continuous, card-indexed evaluation** with an explicit win-condition race
   model (Section 4),
4. a **deliberate patron draft** (Section 5).

---

## 2. Determinized-ensemble turn-plan search

**Ensemble.** At the start of each turn the agent draws K = 14 seeds and builds
K determinized worlds via `GameState.ToSeededGameState(seed)`. The seed set is
drawn **once per turn** and reused for every decision and every root move in
that turn. This is a common-random-numbers design: root-move comparisons are
*paired* across worlds, so the ranking is not polluted by the variance between
which worlds each candidate happened to be evaluated in.

**Per-world search.** Each world gets its own PUCT-guided best-first tree over
the current turn's move sequences. Two properties distinguish it from a standard
MCTS:

- **Max backup, not mean.** A node's value is the maximum over its expanded
  children, because within a fixed determinized world the agent controls every
  transition. Averaging would price in decisions it will not be forced to make.
- **Exact solving and solved-subtree pruning.** A child is marked *solved* when
  it can no longer change: it ends the turn, it hit the depth or node cap, or its
  entire subtree is solved. Solved children are removed from selection, and a
  node all of whose continuations are solved becomes a leaf. When every world's
  plan space is solved the search returns immediately — it stops because there is
  nothing left to find, not because it ran out of clock. This convergence signal
  is what the time manager in Section 3 is built on.

The K trees are advanced **round-robin**, one iteration each, so the budget is
split evenly and every tree stays anytime-usable; a world that solves out drops
from the rotation and hands its share to the rest.

**Priors and rollouts.** A hand-written move-ordering policy supplies PUCT
priors (softmax at a deliberately broad temperature of 25, so the prior biases
the search rather than deciding it) and also serves as the greedy rollout policy
to end of turn. The same policy is the panic fallback when the clock is nearly
exhausted.

**Branch control.** Root and normal-state branching are capped at 22 moves by
policy score, choice states at 14; `END_TURN` is always retained. This caps the
`MAKE_CHOICE` combinatorial explosion without ever *forcing* a move — in
particular, **combo ordering is never taken away from the search**: no card is
played on the move-ordering policy's say-so alone.

**Root aggregation.** Per-world root values are collapsed by a selectable rule:
mean over worlds, majority vote of the per-world best moves with the mean as
tiebreak, or mean penalised by cross-world standard deviation. A small breadth
bonus favours moves every world bothered to examine. The vote and penalty rules
exist because averaging per-world *optima* is strategy fusion: it implicitly
prices in an ability to adapt to whichever world turns out to be real, which the
agent does not have.

**Shipped setting:** `Aggregation = 0` (mean over worlds), `BreadthBonus =
0.0005`, `DisagreementLambda = 1.0` (inert at `Aggregation = 0`). Mean is the
configuration behind every benchmark number in Section 8; the vote and penalty
rules are present in the submitted source but are not selected at runtime.

**Determinization sampling.** Two modes: K i.i.d. seeds, or stratified sampling
— oversample candidate worlds, rank them by the value of the next few cards the
agent would draw, split into K equal-count strata and take one world uniformly
from each. The stratification key is computed only from the agent's own deck,
and each candidate is a world the engine itself produced from a seed the agent
chose, so nothing is conditioned on unobservable information. Stratification is
unbiased for the same ensemble mean with the between-strata variance removed;
candidate generation is bounded both by a wall-clock cap (0.15 s) and by a
quarter of the turn budget, and degrades to i.i.d. rather than overrunning.

**Shipped setting:** `Stratify = 0` — K i.i.d. seeds. Stratified sampling is
present in the submitted source but is not enabled.

---

## 3. Adaptive time management

The 10 s limit is **cumulative over every `Play()` call in a turn**, and an
overrun is an instant loss. The agent therefore measures all of its own overhead
— determinization, prior computation and fallback included — from the top of
each call, and self-imposes a ceiling of 9.3 s, further tightened by whichever
is smaller of the engine-reported remaining time and a 0.70 s safety margin.

Each decision's slice is a boosted even share (`SliceBoost = 1.5`) of *what is
actually left* over the number of decisions the turn is still expected to
contain. Because the remaining budget is recomputed from the engine's clock on
every call, a decision that solved in 40 ms hands its unused share directly to
later decisions — and the decisions that collect it are precisely the ones still
unsolved.

Two **measured floors** raise the slice on slow or contended hardware:

- the EWMA cost of an actual *solve* on this machine, computed only over
  converged decisions so it can never be contaminated by a slice the agent cut
  short itself; and
- the live iteration rate, so a slice always affords several full root sweeps per
  determinization. Below that threshold most root moves carry a single-world
  estimate, which is worse than the greedy policy.

Four independent **ceilings** bound it: an absolute per-decision cap (2.5 s), a
fixed fraction of what is left (25%, so the remaining budget decays
geometrically and no misestimate of the decision count can strand the turn's
tail on the fallback), a learned overrun guard, and a hard bound that always
preserves the greedy escape hatch.

The **overrun guard** is a decayed maximum of how far past its own deadline the
search actually ran. The search polls the clock at every node and every rollout
step, so this measures the longest uninterruptible unit of work — one
`ApplyMove` — on the host machine, which is exactly the quantity that would eat
the margin to the 10 s wall under load. It is deliberately *not* reset between
games: it is a safety margin, and re-learning it from scratch every game means
being wrong once on the one clock where being wrong loses the match.

This design is only safe because the search is convergence-aware. A generous
slice costs nothing when there is nothing left to find, so "search longer when
unsolved, stop dead when solved" falls out of the two mechanisms together.

Robustness: every decision is wrapped so that any exception falls back to the
greedy policy and then to the first non-`END_TURN` legal move; a decision with
under 0.30 s left skips the search entirely.

**Measured behaviour.** On the reference machine (macOS, `-to 10`) the agent
makes **7–12 searched decisions per turn, mean ≈ 9.5**, at roughly **37 000
search iterations per second**; both quantities are tracked live at runtime and
the slice floors scale in proportion, so a machine at a fraction of that rate
receives a proportionally larger slice. Across the 192-game submission gauntlet
in Section 8 the agent recorded **zero** turn timeouts, bot exceptions and
illegal moves — every game in the gauntlet reported *ends due to other factors:
0*, and the agent's self-report line (Section 7) never fired. The independent
validation run for this submission (3 games vs BestMCTS3, seeds 77–79, `-to 10`)
likewise ended 0/3 on other factors with no output on stderr.

---

## 4. Evaluation

The leaf evaluation returns a win-probability-like score in (0,1) from a fixed
perspective, squashed through a logistic. Every backup site uses the same scale
— there is no negation or zero-sum mixing anywhere in the search.

**Card values.** A continuous 0–100 "quality as a deck slot" measure, held in
flat arrays indexed by `(int)CardId` (no string scans or dictionary lookups on
the hot path), plus a separate in-hand value with an immediate-use bias. Curses
are negative. A continuous scale rather than a small number of tiers means ties
are rare and marginal buy decisions have real resolution.

**Win-condition race.** Prestige is weighted by a term that grows with how far
along the race the leaders are, and there is an *explicit* 40-prestige model:
crossing 40 while strictly ahead wins at the start of the next turn unless the
opponent matches during their single reply, so it carries a large bonus, and the
mirrored case a larger penalty. Graduated urgency applies on **both** sides
within 12 points of the threshold, and again in the 76–79 window before 80. Mid
turn, unspent Power is counted as prestige-to-be, net of whatever the opponent's
taunt agents will soak up.

**Patrons.** Scored two-sidedly: favour differential, plus separate
distance-to-victory tables for how many patron calls each side needs to sweep.
Holding Duke of Crows carries a penalty that decays with game progress (holding
the favour locks the agent out of calling it again); Ansei favour carries a
bonus.

**Deck quality and combo density.** Deck score is the summed card value
normalised by a square-root dilution term, plus a genuine combo-density term:
for each patron deck, the probability of drawing ≥2, ≥3 and ≥4 cards of that
deck into a five-card hand, mixed and weighted by that deck's combo payoff
strength. This is a distributional quantity, not a power law on deck counts.

**Other terms.** Own and enemy agents (card value, current HP, taunt); tavern
denial weighted by how much of the opponent's deck matches each tavern card's
patron; own coins and patron calls during the agent's own turn; a discounted term
for the opponent's banked coins otherwise; and the known upcoming draws.

Thread safety: the two scratch buffers are `[ThreadStatic]`, since the runner
may construct one bot instance per worker thread.

**Weights.** Every weight is **hand-set from design reasoning and compiled in**;
no weight in the submitted file was produced by an automatic tuner, and no
learned artefact ships with the agent. The development tree can overlay the
weight table from an external JSON file, which is how candidate values were
explored, but no tuned set was promoted: the submitted file's literals are the
hand-set defaults, and they are the configuration all of Section 8 was measured
on. The packaging step removes the overlay mechanism outright rather than
disabling it, so the submitted agent cannot pick up a weight file even if one is
present.

---

## 5. Patron draft

A fixed preference order, chosen deliberately rather than searched:

**Orgnum → Saint Alessia → Pelin → Hlaalu → Rajhin → Red Eagle → Ansei → Psijic
→ Duke of Crows.**

Orgnum leads because its deck is cheap, prestige-dense and combos with itself,
and it is the patron this evaluation prices highest at the draft stage. Saint
Alessia is second because it is the newest deck and the one this agent's card
values price furthest from a conventional tier list, so drafting it is where the
valuation has the most room to pay off. Patrons outside the 2026 pool are listed
so the draft stays legal if the pool changes, and the selector falls back to the
first available patron rather than ever returning an illegal choice or hanging.

---

## 6. How this differs substantially from 2025 entries

The competition requires an entry to be substantially different from other
people's 2025 bots. The 2025 field this agent was benchmarked against
(SakkirinaSolo, Sakkirina, HQL_Plus_BOT, BestMCTS3, EBot_C, EBot_F) is built
from MCTS with rule-based move restriction, or from Q-learning over a
hand-designed feature space.

**The combination that is new here is a determinized-ensemble, full-turn-plan
search with exact solving, driven by an adaptive throughput-aware time manager,
scoring leaves with a continuous `CardId`-indexed evaluation.** Each of those
three pieces appears in the literature in some form; no 2025 entrant used them
together, and the three are mutually dependent rather than merely co-present:
the search's convergence signal is what makes a generous adaptive slice safe,
the adaptive slice is what lets the ensemble be searched to convergence on
hardware the author does not control, and the continuous evaluation is what
gives the ensemble mean enough resolution for paired root comparisons to
separate at K = 14.

Component by component, stated as differences of construction and not as
judgements of the other entries:

| | 2025 field | This agent |
|---|---|---|
| Within-turn model | two-player MCTS with chance nodes and mean backup | single-agent planning with max backup and **exact solving** of the turn plan space |
| Hidden information | single determinization, or chance-node progressive widening | K = 14 determinized worlds searched in lockstep under **common random numbers**, so root comparisons are paired |
| Root decision | most-visited / highest-mean child | ensemble aggregation over per-world root values (mean shipped; consensus vote and a disagreement penalty also implemented) |
| Combo ordering | restricted by a rule base outside the search | **stays inside the search**; the branch cap prunes by policy score but never forces a move |
| Card valuation | discrete tier buckets | continuous 0–100 scale, `CardId`-indexed flat arrays |
| Combo value | power law on deck counts | **hand-density probabilities** P(X≥2,3,4) into a five-card hand |
| Win condition | reaction concentrated in the 76–79 window | **explicit 40-prestige race model** with graduated urgency on both sides |
| Patron pressure | defensive | **two-sided**: distance-to-sweep scored offensively and defensively |
| Time management | fixed fraction of remaining time | **hardware-adaptive**: throughput- and solve-cost-derived floors, four independent ceilings, a learned overrun guard, and unused budget redistributed within the turn |
| Draft | tier-list or trained preference | fixed order derived from this agent's own card and patron valuation |

No code, tier list, weight table or learned artefact from any 2025 entry is
used. The 2025 sources are public and were read as background — that reading is
what set the comparison baseline above — but every line of the submitted file
was written for this entry.

---

## 7. Resource behaviour

- **Time:** self-imposed 9.3 s ceiling against the 10 s cumulative turn limit,
  with a 0.70 s margin and a learned overrun guard on top. Turn timeouts across
  the 192-game submission gauntlet: **zero**.
- **Memory:** the search allocates a bounded transient tree — at most 16 000
  plan nodes per decision across all K worlds (on the order of 25 MB), freed
  when the decision returns. There is no tree reuse across decisions or turns
  and no persistent per-game state beyond a handful of scalars, so steady-state
  usage is that bound plus the runtime's own footprint.
- **I/O:** none. The agent opens no files, reads no environment variables,
  spawns no processes, creates no threads and uses no native interop. It writes
  a single line to stderr only if it loses a game to its own timeout or
  exception, and never writes to stdout. That line did not fire in any benchmark
  or validation game.

---

## 8. Results

**Methodology.** All matches are **seeded mirror pairs**: each seed is played
twice with the seats swapped, so seat advantage cancels within a pair and the
two agents see the same shuffles. Time control is the competition's `-to 10`
(10 s cumulative per turn). Results are **gated on abnormal ends** — any game
that terminates for "other factors" (turn timeout, bot exception, illegal move)
is flagged and excluded from the clean win rate rather than being counted as a
win, so a win rate here never includes a game won because the opponent's process
misbehaved. Confidence intervals are Wilson 95%.

**Head-to-head, submission build vs the 2025 field** (per-opponent batches of 16
games each):

| Opponent | 2025 field rank (win rate) | Result | Win rate | Wilson 95% CI | Abnormal ends |
|---|---|---|---|---|---|
| SakkirinaSolo | 1st (71.8%) | 11–5 | **68.8%** | 44.4–85.8% | 0 |
| Sakkirina | 2nd (70.6%) | see note | **≈58–62%** (clean games only) | — | opponent-side, see note |
| HQL_Plus_BOT | 3rd (69.2%) | 11–5 | **68.8%** | 44.4–85.8% | 0 |
| BestMCTS3 | 4th (66.1%) | 8–8 | **50.0%** | 28.0–72.0% | 0 |
| EBot_C | 5th (56.0%) | 13–3 | **81.2%** | 57.0–93.4% | 0 |
| EBot_F | 6th (51.8%) | 11–5 | **68.8%** | 44.4–85.8% | 0 |

**Note on Sakkirina — reported honestly.** On the benchmark host, Sakkirina
terminated abnormally (its own timeout or crash) in up to half of the games
played against it. Every one of those abnormal ends was scored as a Sakkirina
loss by the runner. Counting them would inflate this agent's win rate on a
result it did not earn, so they are excluded and only the clean games are
reported, which puts the figure in the **58–62%** band. This appears to be an
environment-specific fragility of that opponent on this host rather than
anything the agent does — the agent's own abnormal-end count against it was
zero, and forensic replays (seeds 9200–9209) confirmed every flagged game ended
on the opponent's side. The band rather than a point estimate reflects the small
clean-game count that survives the gate.

**Aggregate field performance.** Pooling the gauntlet: **66.7% (128/192)**,
Wilson 95% CI **59.7–72.9%**. The gauntlet was run as two independent 96-game
halves and returned 66.7% on each half separately, which is the main reason to
believe the aggregate rather than the individual per-opponent cells — at 16
games apiece those cells have very wide intervals, and the 50.0% vs BestMCTS3
and the 81.2% vs EBot_C are not separated by anything like their spread.

**Comparison to the 2025 final standings, with the caveat stated.** The 2025
champion's published field win rate was 71.8%. That number is **not directly
comparable** to the 66.7% above: the 2025 standings were computed over the full
2025 field, which included weaker entrants that are not present in this
gauntlet, so it is a field-strength difference and not only a strength
difference between agents. The honest reading of the table is narrower and does
not depend on that comparison: against the top four 2025 finishers played
directly and head to head under mirror pairs, this agent is at or above even in
every matchup measured, and its aggregate over the six-bot field is 66.7% with
zero self-inflicted losses.

**Reproducibility.** The submitted file was compiled standalone into a
`ScriptsOfTribute-Core` `Bots` project containing no other source from this
project, with zero compiler warnings attributable to it, and re-validated with 3
fresh games vs BestMCTS3 (seeds 77–79, `-to 10`): 3 wins, all by the 40-prestige
condition, 0/3 ends due to other factors, and no stderr output.

---
