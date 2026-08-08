# [AGENT NAME TODO] — Technique Description

**IEEE CoG 2026 Tales of Tribute AI Competition**

- **Agent name:** `TODO` — placeholder class name in the submitted source is `Riptide`
- **Authors / affiliation:** `TODO`
- **Contact:** `TODO`
- **Submission:** single C# file, compiles into the `ScriptsOfTribute-Core` `Bots`
  project (netstandard2.1, LangVersion 10) with no added dependencies
- **Language / runtime:** C# 10, .NET Standard 2.1, no external packages

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
4. a **deliberate patron draft** aimed at the decks the 2025 field prices badly
   (Section 5).

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
particular, **combo ordering is never taken away from the search**, which is the
single largest structural difference from the 2025 champion (Section 6).

**Root aggregation.** Per-world root values are collapsed by a selectable rule:
mean over worlds (shipped), majority vote of the per-world best moves with the
mean as tiebreak, or mean penalised by cross-world standard deviation. A small
breadth bonus favours moves every world bothered to examine. The vote and
penalty rules exist because averaging per-world *optima* is strategy fusion: it
implicitly prices in an ability to adapt to whichever world turns out to be
real, which the agent does not have.

> **TODO (final tuning):** state which aggregation rule ships and the measured
> effect. Current default: mean, `BreadthBonus = 0.0005`.

**Determinization sampling.** Two modes: K i.i.d. seeds (shipped), or
stratified sampling — oversample candidate worlds, rank them by the value of the
next few cards the agent would draw, split into K equal-count strata and take one
world uniformly from each. The stratification key is computed only from the
agent's own deck, and each candidate is a world the engine itself produced from
a seed the agent chose, so nothing is conditioned on unobservable information.
Stratification is unbiased for the same ensemble mean with the between-strata
variance removed; candidate generation is bounded both by a wall-clock cap and
by a quarter of the turn budget, and degrades to i.i.d. rather than overrunning.

> **TODO (final tuning):** state whether stratified sampling ships.

---

## 3. Adaptive time management

The 10 s limit is **cumulative over every `Play()` call in a turn**, and an
overrun is an instant loss. The agent therefore measures all of its own overhead
— determinization, prior computation and fallback included — from the top of
each call, and self-imposes a ceiling of 9.3 s, further tightened by whichever
is smaller of the engine-reported remaining time and a 0.70 s safety margin.

Each decision's slice is a boosted even share of *what is actually left* over
the number of decisions the turn is still expected to contain. Because the
remaining budget is recomputed from the engine's clock on every call, a decision
that solved in 40 ms hands its unused share directly to later decisions — and
the decisions that collect it are precisely the ones still unsolved.

Two **measured floors** raise the slice on slow or contended hardware:

- the EWMA cost of an actual *solve* on this machine, computed only over
  converged decisions so it can never be contaminated by a slice the agent cut
  short itself; and
- the live iteration rate, so a slice always affords several full root sweeps per
  determinization. Below that threshold most root moves carry a single-world
  estimate, which is worse than the greedy policy.

Four independent **ceilings** bound it: an absolute per-decision cap, a fixed
fraction of what is left (so the remaining budget decays geometrically and no
misestimate of the decision count can strand the turn's tail on the fallback), a
learned overrun guard, and a hard bound that always preserves the greedy escape
hatch.

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

> **TODO (final benchmarks):** report turn-timeout / bot-exception counts across
> the full benchmark set, and the observed decisions-per-turn and
> iterations-per-second distributions on the reference machine.

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

> **TODO (final weights):** summarise the tuned weight set — which knobs moved
> most from their hand-set defaults, the tuning method and budget, and the
> measured win-rate delta from tuning. Every weight ships compiled in; the
> submitted file contains no external configuration.

---

## 5. Patron draft

A fixed preference order, chosen deliberately rather than searched:

**Orgnum → Saint Alessia → Pelin → Hlaalu → Rajhin → Red Eagle → Ansei → Psijic
→ Duke of Crows.**

Orgnum first is the one point of agreement between the two 2025 bots that beat
the champion head-to-head, and its deck is cheap and prestige-dense. Saint
Alessia second because the 2025 field is structurally blind to it — BestMCTS3
refuses to draft it and HQL_Plus has essentially no trained values for it — and
this agent prices it properly. Patrons outside the 2026 pool are listed so the
draft stays legal if the pool changes, and the selector falls back to the first
available patron rather than ever returning an illegal choice or hanging.

---

## 6. How this differs substantially from 2025 entries

The competition requires an entry to be substantially different from other
people's 2025 bots. The 2025 field this agent was designed against
(SakkirinaSolo, Sakkirina, HQL_Plus_BOT, BestMCTS3) is built from MCTS with
rule-based pruning, or from Q-learning over a hand-designed feature space. The
differences here are architectural, not parametric:

| | 2025 field | This agent |
|---|---|---|
| Within-turn model | two-player MCTS with chance nodes and mean backup | single-agent planning with max backup and **exact solving** of the turn plan space |
| Hidden information | single determinization, or chance-node progressive widening | K = 14 determinized worlds searched in lockstep under **common random numbers**, so root comparisons are paired |
| Root decision | most-visited / highest-mean child | selectable ensemble aggregation, including consensus vote and a **disagreement penalty** that prices strategy fusion |
| Combo ordering | force-played by a rule base outside the search — the documented weak point of the 2025 champion | **stays inside the search**; the branch cap prunes by policy score but never forces a move |
| Card valuation | discrete tier buckets | continuous 0–100 scale, `CardId`-indexed flat arrays |
| Combo value | power law on deck counts | **hand-density probabilities** P(X≥2,3,4) into a five-card hand |
| Win condition | reaction only in the 76–79 window | **explicit 40-prestige race model** with graduated urgency on both sides, the far more decisive threshold |
| Patron pressure | defensive only | **two-sided**: distance-to-sweep scored offensively and defensively |
| Time management | flat fraction of remaining time | **hardware-adaptive**: throughput- and solve-cost-derived floors, four independent ceilings, a learned overrun guard, and unused budget redistributed within the turn |
| Draft | tier-list or trained preference; Alessia avoided | deliberately targets the decks the field misprices |

No code, tier list, weight table or learned artefact from any 2025 entry is used.
The 2025 sources were read and analysed — that analysis is what identified the
combo-ordering hole and the Saint Alessia blind spot — but the implementation is
original.

---

## 7. Resource behaviour

- **Time:** self-imposed 9.3 s ceiling against the 10 s cumulative turn limit,
  with a 0.70 s margin and a learned overrun guard on top. `TODO`: report
  observed timeout count across the final benchmark set (expected: zero).
- **Memory:** the search allocates a bounded transient tree — at most 16 000
  plan nodes per decision across all K worlds, freed when the decision returns.
  There is no tree reuse across decisions or turns and no persistent
  per-game state beyond a handful of scalars. `TODO`: report the measured
  steady-state figure on the reference machine.
- **I/O:** none. The agent opens no files, reads no environment variables,
  spawns no processes, creates no threads and uses no native interop. It writes
  a single line to stderr only if it loses a game to its own timeout or
  exception, and never writes to stdout.

---

## 8. Results

> **TODO (final benchmarks):** head-to-head win rates with binomial confidence
> intervals against the 2025 field (SakkirinaSolo, Sakkirina, HQL_Plus_BOT,
> BestMCTS3, EBot_C, EBot_F), the number of seeded mirror-match pairs behind
> each, and the aggregate field win rate for comparison against the 2025 final
> standings (SakkirinaSolo 71.8%, Sakkirina 70.6%, HQL_Plus_BOT 69.2%,
> BestMCTS3 66.1%). Include the count of games ending due to "other factors",
> which must be zero.

---

## 9. AI tool disclosure

> **TODO:** state per the competition's requirements what, if anything, was
> produced with AI assistance.
