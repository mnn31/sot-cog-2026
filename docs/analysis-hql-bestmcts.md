# Technical Analysis: HQL_Plus_BOT, BestMCTS3, and MaltheMCTS
## 2025 IEEE COG Tales of Tribute AI Competition (Scripts of Tribute v1.1.1)

**Purpose:** reverse-engineer the top-performing 2025 agents so we can build a stronger bot for the 2026 edition. All source references are to
`ScriptsOfTribute-CompetitionsArchive/competition-2025-08-COG/cs_agents/`.

---

## 0. Context: the 2025 field and the rules that shaped it

### Final standings (5440 games each, all-play-all mirror matches)

| Rank | Bot | Wins | Losses | Win rate |
|---|---|---:|---:|---:|
| 1 | SakkirinaSolo | 3908 | 1532 | **71.84%** |
| 2 | Sakkirina | 3841 | 1599 | **70.61%** |
| 3 | HQL_Plus_BOT | 3765 | 1675 | **69.21%** |
| 4 | BestMCTS3 | 3594 | 1846 | **66.07%** |
| 5 | EBot_C | 3048 | 2392 | 56.03% |
| 6 | EBot_F | 2817 | 2623 | 51.78% |
| 7 | MCTS_MMHVR | 2798 | 2642 | 51.43% |
| 8 | NAgent | 2741 | 2699 | 50.39% |
| 9 | Council Of Two | 1244 | 4196 | 22.87% |
| 10 | ZMyBot | 1151 | 4289 | 21.16% |
| 11 | MaltheMCTS | 1013 | 4427 | **18.62%** |

Standings were essentially stable from round 1 (see `plots/wr_per_round_cumulative.csv`): SakkirinaSolo opened at 71.25% and finished at 71.67%; MaltheMCTS opened at 22.5% and *declined* to 18.43%. **There is no evidence of a late collapse or a progressive resource leak that only manifests deep into the tournament** — MaltheMCTS was broken from game one.

### Constraints that matter for design

From `ScriptsOfTribute-Core/Engine/src/AI/ScriptsOfTributeGame.cs`:

```csharp
private TimeSpan CurrentTurnTimeRemaining => _timeout - _currentTurnTimeElapsed;
...
var result = CurrentPlayer.Play(state, moves, CurrentTurnTimeRemaining);
```

* **`remainingTime` is per-turn, not per-move and not per-game.** The 10 s budget is decremented by the wall time of every `Play()` call inside a turn and reset in `HandleEndTurn()`. A turn commonly needs 5–25 `Play()` calls, so a bot must self-ration.
* **Exceeding the budget is an instant loss** — `task.Wait(CurrentTurnTimeRemaining)` failing returns `EndGameState(_api.EnemyPlayerId, GameEndReason.TURN_TIMEOUT)`.
* **Any uncaught exception is also an instant loss** — `GameEndReason.BOT_EXCEPTION`, opponent wins.
* **An illegal move is an instant loss** — `GameEndReason.INCORRECT_MOVE`.
* Memory: 256 MB soft ceiling, 1024 MB hard exclusion (competition README). Not enforced by the engine itself.
* Turn limit 500 (`ScriptsOfTributeGame.TurnLimit`), draw on exceed.

### Patron draft mechanics (`ScriptsOfTribute.cs::PatronSelection`)

Snake draft over a 6-patron pool (Pelin, Duke of Crows, Ansei, Rajhin, Orgnum, Saint Alessia — Treasury excluded and auto-added):

| Call | Picker | `availablePatrons.Count` | `round` |
|---|---|---:|---:|
| 1 | Player 1 | 6 | 1 |
| 2 | Player 2 | 5 | 1 |
| — | *(Treasury inserted)* | — | — |
| 3 | Player 2 | 4 | 2 |
| 4 | Player 1 | 3 | 2 |

So the game is **P1, P2, P2, P1** — the second player gets consecutive picks. This detail is exploitable and, as shown below, all three bots analysed here handle the draft badly or accidentally.

---

## 1. HQL_Plus_BOT — 3rd place, 69.21%

**Files:** `HQL_Plus_BOT.cs` (427), `QL.cs` (646), `EnumsConstants.cs` (48), `NoChoiceMoves.cs` (148), `QTABLE1.txt` / `QTABLE2.txt` (2390 / 2393 lines).

### 1.1 Architecture — it is *not* an RL agent, it is a beam search with a learned card-value table

The name misleads. There is no Q-learning policy that selects moves. The double-Q-learning machinery exists solely to maintain a **card-value lookup table that feeds one term of a hand-written static evaluation function**, which in turn scores the leaves of a **greedy beam search**. Concretely:

**Move-type dispatch (`HQL_Plus_BOT.Play`)** — three disjoint regimes:

1. **Only `END_TURN` available** → end turn, run bookkeeping.
2. **Any `PLAY_CARD` / `ACTIVATE_AGENT` available** → **no search at all.**
   ```csharp
   best_move = action_agent_moves[0];
   var no_choice_move = HQL_Plus_FindNoChoiceMove(sgs, action_agent_moves);
   if (no_choice_move is not null) best_move = no_choice_move;
   ```
   Play the first whitelisted "no-choice" card if one exists, otherwise the *first move in the engine's list*. Card-play ordering is effectively arbitrary.
3. **Otherwise (buy / patron-call / end-turn phase)** → beam search + ensemble vote.

This is the single most important structural fact about HQL: **it only searches over the purchase and patron-call phase.** The action-play phase — where the combo chains, agent attacks, and choice resolutions live — is handled by a static whitelist. That keeps the branching factor tiny and the bot fast, and it is a large part of why HQL never times out. It is also its biggest exploitable weakness.

**Beam search (`HQL_Plus_BestMoveFromSearch`)**

```csharp
private int nodes_limit = 15;
```

* Root: expand **all** legal moves (no beam at the root), applying each with a fresh random seed `sgs.ApplyMove(move, (ulong)rng.Next())`. `END_TURN` produces a terminal node carrying the current state.
* Iterate: expand every surviving node's full move list, then keep the **top 15 by heuristic score** using a linear min-replacement scan. Terminal (`END_TURN`) nodes are siphoned into `end_states_nodes`.
* Before a node enters the beam, `HQL_Plus_PlaySimpleMovesOnNodeUntilChoice` **auto-plays whitelisted no-choice cards until a real decision appears**, compressing the tree.
* Terminate when the beam empties (every line has ended its turn). Return the *root move* of the best terminal node.

Each node stores a full `SeededGameState`, but the beam is capped at 15 and discarded per call — HQL is by far the most memory-frugal of the three bots.

**Determinization ensemble (the clever bit)**

```csharp
const int numberOfSearches = 7;
var moveVotes = new Dictionary<Move, int>();
for (int i = 0; i < numberOfSearches; i++) {
    Move move = HQL_Plus_BestMoveFromSearch(sgs, possibleMoves);
    ...
}
best_move = moveVotes.OrderByDescending(kvp => kvp.Value).First().Key;
```

Seven independent beam searches, each re-randomising the hidden information and every stochastic effect (`ApplyMove(move, rng.Next())`), then a **plurality vote**. This is root-parallel determinization on the cheap: no belief model, no chance nodes, just variance reduction by resampling. It is robust, trivially implementable, and worth borrowing.

### 1.2 Evaluation function (`QL.HQL_Plus_Heuristic`)

Returns `player_score − enemy_score` as an `int`. Every weight is indexed by a 5-valued game stage.

**Stage function (`HQL_Plus_TransformGameStateToStages`)** — note it keys off the *enemy's* prestige plus our own turn counter:

```csharp
int sum = sgs.EnemyPlayer.Prestige + turn_counter;
// 0–9 Start | 10–19 Early | 20–29 Middle | 30+ Late
if (sgs.EnemyPlayer.Prestige > 40) stage = Stage.End;
```

**Weight table (`EnumsConstants.cs`, index = `[Start, Early, Middle, Late, End]`):**

```csharp
prestige_weight     = { 15,  16,  20,  22,  100 }
power_weight        = { 14,  15,  19,  19,   20 }
coins_weight        = {  2,   1,   1,   1,    0 }
patron_weight       = { 18,  18,  18,  18,   14 }
card_weight         = {  5,   5,   4,   4,    2 }
combo_weight        = {  2,   2,   2,   2,    1 }
active_agent_weight = { 12,  12,  12,  12,   12 }
hp_weight           = {  4,   4,   4,   4,    3 }
ansei_weight        = {  3,   2,   1,   1,    1 }
crow_weight         = { -1000, -1000, 0, 20, 20 }   // <-- dominant term
orgnum_weight       = {  1,   1,   3,   5,    5 }
gold_card_weight    = {  0,  -2,  -4,  -6,   -8 }
```

Key readings:

* **`crow_weight = −1000` in Start and Early.** Owning Duke of Crows favour before the midgame is treated as a near-catastrophe — it swamps everything else in the function (a whole 20-card deck is worth roughly `20 × 15 × 5 ≈ 1500`). It flips to `+20` in Late/End. This is a hard-coded expression of the Crows tempo rule: call it early and you convert coins to power at a loss; call it when you can bank ≥7 coins and it wins games.
* **`prestige_weight` jumps 22 → 100 at the End stage**, i.e. once the enemy is past 40. Everything else is abandoned in favour of racing prestige.
* **`gold_card_weight` goes negative and steepens** (`0, −2, −4, −6, −8`): Gold cards are deck bloat and are actively penalised later.
* **Patron favour is cheap** (18) relative to a single deck card (`q ≈ 15 × card_weight 5 ≈ 75`). HQL is a deck-quality bot first, patron bot second — except for the loss-avoidance clamp:

```csharp
if (enemy_patrons == 2)      enemy_score += patron_weight[stage] * 2;
else if (enemy_patrons == 3) enemy_score += patron_weight[stage] * 4;
else if (enemy_patrons == 4) enemy_score += patron_weight[stage] * 100;  // = 1800 at stage 0
```

Superlinear enemy-patron dread with a hard cliff at 4 (the patron win condition). Note there is **no symmetric bonus for our own patron count** — asymmetric, defensive.

* Agents: `active_agent_weight[stage] * q_value + CurrentHp * hp_weight[stage]` → an S-tier agent with 4 HP is worth `12 × 25 + 4 × 4 = 316`, i.e. ~4 average deck cards. Agents are heavily valued.

**Two real bugs in the evaluation, both exploitable:**

```csharp
List<UniqueCard> all_enemy_cards = sgs.EnemyPlayer.Hand
    .Concat(sgs.EnemyPlayer.DrawPile)
    .Concat(sgs.CurrentPlayer.Played.Concat(sgs.CurrentPlayer.CooldownPile))  // <-- CurrentPlayer, not Enemy
    .ToList();
```

1. **The enemy combo count is computed partly from our own played and cooldown piles.** Copy-paste error. The enemy-side combo term is garbage.
2. **The enemy deck's card quality is never evaluated at all.** Our cards get `q_value * card_weight`; enemy cards only contribute `combo_weight`. HQL is blind to the opponent building a strong deck; it only reacts to opponent prestige, power, patrons, and agents. **An opponent that quietly assembles a high-quality engine is invisible to HQL until the prestige actually lands.**

### 1.3 The Q-tables — what they actually contain

`QTABLE1.txt`/`QTABLE2.txt` are `cardId : deckCardCount : value` triples, 123 card IDs × 20 states (deck-count 0–19, `max_deck_cards = 19`). Missing keys default to `5`; the value used in the heuristic is

```csharp
double avg = (q1 + q2) / 2.0;
int q = (int)avg;          // truncation, not rounding
if (q == 0) q = 1;         // floor
```

**Learned card values (mean over states, both tables averaged) — top of the list:**

| Card | mean Q | | Card | mean Q |
|---|---:|---|---|---:|
| RALLY | **26.82** | | MURDER_OF_CROWS | 17.44 |
| THE_ARMORY | **24.54** | | SEA_SERPENT_COLOSSUS | 17.32 |
| MIDNIGHT_RAID | **23.07** | | HOSTILE_TAKEOVER | 17.28 |
| SIEGE_WEAPON_VOLLEY | 22.08 | | SUMMERSET_SACKING | 16.94 |
| HIRAS_END | 22.06 | | PYANDONEAN_WAR_FLEET | 16.89 |
| MARCH_ON_HATTU | 21.05 | | SERPENTGUARD_RIDER | 16.89 |
| KNIGHT_COMMANDER | 20.79 | | SCRATCH | 16.65 |
| ARCHERS_VOLLEY | 20.57 | | CUSTOMS_SEIZURE | 16.63 |
| CONQUEST | 19.78 | | BANGKORAI_SENTRIES | 16.53 |
| CURRENCY_EXCHANGE | 19.37 | | HAGRAVEN_MATRON | 16.33 |
| LEGIONS_ARRIVAL | 19.20 | | GRAND_LARCENY | 16.28 |
| BANNERET | 18.81 | | SQUAWKING_ORATORY | 16.18 |
| REINFORCEMENTS | 18.07 | | OATHMAN | 16.12 |
| ANSEIS_VICTORY | 17.98 | | HEL_SHIRA_HERALD | 16.08 |
| THE_PORTCULLIS | 17.95 | | SERPENTPROW_SCHOONER | 16.04 |

Bottom (excluding zeros): `HLAALU_COUNCILOR 5.17`, `HLAALU_KINSMAN 6.43`, `MORIHAUS_THE_ARCHER 6.92`, `ALESSIAN_REBEL 7.25`, `PRIESTESS_OF_THE_EIGHT 7.67`, `PELINAL_WHITESTRAKE 1.02`, `WHITESTRAKE_ASCENDANT 1.44`.

**The critical defect: 66 of 125 card IDs have Q = 0.0 for all 20 states.** These are cards the training run never scored, and every one of them collapses to the effective floor `q = 1`. The zero set includes real cards that matter in 2025:

`KING_ORGNUMS_COMMAND, SEA_ELF_RAID, SEA_RAIDERS_GLORY, MAORMER_CUTTER, GRAND_ORATORY, WAY_OF_THE_SWORD, BATTLE_MEDITATION, IMPERIAL_PLUNDER, IMPERIAL_SPOILS, HARVEST_SEASON, BLACKMAIL, AMBUSH, BLACK_SACRAMENT, BARTERER, RAGPICKER, TITHE, IMPRISONMENT, WRIT_OF_COIN, GOLD, LAW_OF_SOVEREIGN_ROOST, PECK, SWIPE, RINGS_GUILE, MOONLIT_ILLUSION, BEWILDERMENT, SHIELD_BEARER, FORTIFY, KWAMA_EGG_MINE, EBONY_MINE, GOODS_SHIPMENT, ...` plus all 12 Psijic cards (correctly, since Psijic is not in the pool).

Contract actions and Treasury cards are systematically at zero, which means **HQL values every contract action and every Treasury card identically at the floor** — Blackmail, Ambush, Harvest Season, Ragpicker, Tithe, Writ of Coin and Gold are indistinguishable to it. Its purchases in that space are driven only by the incidental prestige/power/coin/combo terms.

The Alessia deck is also poorly covered: `CHAINBREAKER_SERGEANT` has data for only 17 of 20 states, `MORIHAUS_THE_ARCHER` 12, `PELINAL_WHITESTRAKE` **2**, `WHITESTRAKE_ASCENDANT` 9, and `AYLEID_DEFECTOR`/`AYLEID_QUARTERMASTER` are absent entirely (falling back to the default `5`). **HQL is materially undertrained on the newest deck.**

### 1.4 Learning behaviour at run time

`explorationChance = 0.5` is declared and never used. The live parameters are:

```csharp
private static double learningRate  = 0.1;
private static double discountFactor = 0.5;
double double_q_learning_alpha = 0.5;  // P(update table 1) vs table 2
```

Update (`HQL_Plus_CalculateNewQValue`), a textbook Double-Q update with the table chosen by a coin flip:

```csharp
new_q = (1 - 0.1) * current_q + 0.1 * (card_score + 0.5 * next_state_value);
```

`card_score` comes from `HQL_Plus_ScoreForCompletedAction`, which sums per-action rewards over the turn for every card the bot has actually acquired:

```csharp
int power_prestige_w = 2;   int coin_w = 1;
int average_card_w   = 3;   int max_cost = 10;   int patron_w = 6;
// ACQUIRE_CARD  += TargetCard.Cost
// GAIN_COIN     += Amount * 1
// GAIN_POWER / GAIN_PRESTIGE / OPP_LOSE_PRESTIGE / HEAL_AGENT += Amount * 2
// DESTROY_CARD  += (10 - TargetCard.Cost) / 2
// DRAW / DISCARD += 3
// REFRESH       += TargetCard.Cost
// ADD_PATRON_CALLS += 6
// ADD_SUMMERSET_SACKING += 2
// KNOCKOUT      -> commented out, contributes 0
```

Two important consequences:

* **`SaveQTableToFile()` is fully commented out.** The shipped tables are frozen artifacts; nothing is written back to disk. However, the in-memory tables are **not** reset in `GameEnd()` (`HQL_Plus_ResetVariables` clears counters, gained cards, and the turn counter but leaves `qTable1`/`qTable2` untouched). So **HQL keeps learning across every game in a single tournament process** — it drifts. Its round-1 win rate was 66.25% and its final was 69.24%, the largest positive drift in the field, which is at least consistent with in-process adaptation.
* `KNOCKOUT` rewards are disabled, so removal effects are never credited.

**Dead code worth noting:** `HQL_Plus_ShouldUseTreasury` and `HQL_Plus_PickCardForTreasury` are defined but never called; `PickBuyMove` is commented out; the Orgnum patron-move filter is commented out.

### 1.5 Patron selection

```csharp
if (availablePatrons.Count == 8 || availablePatrons.Count == 5) {
    ORGNUM > DUKE_OF_CROWS > RAJHIN > RED_EAGLE
} else {
    ORGNUM > RAJHIN        // Crows and Red Eagle branches commented out
}
return availablePatrons.PickRandom(rng);
```

`Count == 8` is a legacy branch from an older, larger patron pool. In 2025 the counts are 6/5/4/3, so:

* **As Player 2's first pick (count = 5)** it uses the full priority list: Orgnum → Crows → Rajhin.
* **In every other slot (6, 4, 3)** it only prefers Orgnum then Rajhin, then picks at **uniform random**.

Net behaviour: **grab Orgnum if it exists, else Rajhin, else (usually) random.** Crows is only actively drafted in one of four draft slots. This is a genuinely weak draft policy that survived to 3rd place — a strong signal that in this game the *draft* is worth far less than *play quality*, or that everyone's draft was equally weak.

Note the interaction with `crow_weight = −1000`: HQL happily leaves Duke of Crows in the pool for the opponent and then treats the opponent holding Crows favour as a *good* thing (`enemy_score += −1000` early). That is coherent with the game rules and quietly effective.

### 1.6 Time and memory management

**There is none.** `remainingTime` is accepted and never read. No `Stopwatch`, no deadline, no fallback move. HQL is safe purely by construction:

* the action-play phase does zero search;
* the buy phase beam is fixed at 15 nodes per level;
* the search runs exactly 7 times per decision;
* whitelisted cards are auto-played inside the search to collapse depth;
* the buy phase's branching factor is bounded by coins and tavern size (~5–15 moves).

Memory footprint is a few hundred `SeededGameState` copies at peak, transient, garbage-collected between calls. HQL is essentially immune to both the 10 s and the 256 MB limits. **That robustness is a strategy, not an accident, and it is the reason a bot with a broken evaluation term and a half-empty value table beats a well-engineered MCTS.**

### 1.7 Duke of Crows guard in `Play()`

```csharp
if (patron.PatronId == PatronId.DUKE_OF_CROWS && stage != Stage.Late && sgs.CurrentPlayer.Coins < 7) {
    possibleMoves.RemoveAt(i);
    break;   // NB: removes only the first such move
}
```

A hard rule layered on top of the search: never call Crows before the Late stage with fewer than 7 coins. Also a contract-action filter that removes any `CONTRACT_ACTION` purchase whose immediate heuristic delta is negative.

---

## 2. BestMCTS3 — 4th place, 66.07% (won 2023 and 2024)

**Files:** `BestMCTS3.cs` (573), `GameStrategy.cs` (415), `TierList.cs` (156), `HandTierList.cs` (128), `AgentTierList.cs` (59), `PatronTierList.cs` (22), `InstantPlayCards.cs` (219). Described in the engineering thesis of Ciężkowski & Krzyżyński.

### 2.1 Architecture — multi-root determinized MCTS with max backup

```csharp
const int maxNoOfRoots = 3;
readonly MCTSNode?[] roots = new MCTSNode?[maxNoOfRoots];
readonly List<ulong> seeds = new();          // 3 fixed seeds, drawn once in the ctor
readonly TimeSpan turnTimeout = TimeSpan.FromSeconds(9.9);
```

**Three parallel determinized trees**, each built from `gameState.ToSeededGameState(seeds[i])` with a *fixed* seed per root, so the three trees are three consistent samples of the hidden information. Moves are compared by summing scores across roots.

**Backup is `max`, not mean:**

```csharp
node.full = isNodeFull;
node.visits += 1;
node.score = Math.Max(node.score, result);   // <-- optimistic / max backup
```

This is the single most important algorithmic choice. Within a turn, Tales of Tribute is effectively a **single-agent planning problem** — the opponent does not interleave, so there is no minimax to average over. Max backup turns the tree into a bounded best-line search, which is exactly right for combo chains. Average backup would dilute a single strong line among many bad siblings. (SakkirinaSolo independently uses max backup too.)

**UCB:**

```csharp
public static double UCBScore(MCTSNode? child, int parentSimulations) {
    if (child is null || child.visits == 0) return double.MaxValue;   // force expansion
    if (child.full) return double.MinValue;                            // solved subtree, never revisit
    return child.score + 1.41 * Math.Sqrt(Math.Log(parentSimulations) / child.visits);
}
```

`c = 1.41 ≈ √2` with `score ∈ [0,1]`. The `full` flag propagates upward when every child is fully solved — a genuine **proof-number-style solved-subtree cutoff**, which lets the search terminate early on small turns (`while (s.Elapsed < budget && !roots[i]!.full)`).

**Rollout (`MCTSNode.Simulate`):** uniform-random moves, `END_TURN` excluded until it is the only option, then `strategy.Heuristic(gameState)`. **Rollouts stop at end of turn, never to game end.** They are cheap and the heuristic does the real work.

### 2.2 Time management — the most sophisticated of the three

```csharp
readonly TimeSpan turnTimeout = TimeSpan.FromSeconds(9.9);   // 100 ms margin
TimeSpan usedTimeInTurn;
```

Per `Play()` call:

1. Start a `Stopwatch`; on `startOfTurn`, reset `usedTimeInTurn` and re-select the strategy phase.
2. **Probe:** run 20 MCTS iterations, recording `maxNoOfMoves` — the deepest move sequence observed. This is a live estimate of *how many more decisions this turn will need*.
3. **Budget:**
   ```csharp
   timeForMoveComputation = (turnTimeout - moveTime.Elapsed - usedTimeInTurn) / Math.Max(2, maxNoOfMoves + 1);
   if (timeForMoveComputation > TimeSpan.FromSeconds(0.5)) timeForMoveComputation = TimeSpan.FromSeconds(0.5);
   ```
   i.e. divide the remaining turn budget by the estimated remaining move count, hard-capped at **0.5 s**.
4. **Degradation ladder.** If less than 5 s remain in the turn, drop to `noOfRoots = 1` and raise the per-move cap to 0.8 s (fewer, deeper searches when time is short).
5. **Panic fallback.** If the computed budget falls below 0.1 s, return a **uniformly random legal move** immediately.

This is a well-engineered anytime scheme: it self-measures rather than assuming a fixed move count, it degrades gracefully, and it never risks the hard timeout. The 0.5 s cap also prevents burning the whole budget on the first decision of a turn.

**Tree reuse and validation.** After choosing move `idx`, all three roots descend: `roots[i] = roots[i]!.children[idx].Item1`. Before reusing, it verifies the tree still matches reality:

```csharp
bool CheckIfSameGameStateAfterOneMove(MCTSNode node, GameState gameState) =>
       CheckIfSameCards(node.gameState.CurrentPlayer.Hand,        gameState.CurrentPlayer.Hand)
    && CheckIfSameCards(node.gameState.TavernAvailableCards,      gameState.TavernAvailableCards)
    && CheckIfSameCards(node.gameState.CurrentPlayer.CooldownPile, gameState.CurrentPlayer.CooldownPile)
    && CheckIfSameCards(node.gameState.CurrentPlayer.DrawPile,     gameState.CurrentPlayer.DrawPile);
```

`CheckIfSameCards` is a multiset comparison on `CommonId`. If a stochastic effect diverged, the root is rebuilt from scratch. **Memory strategy is "reuse within a turn, discard at turn boundary"** — no transposition table, no node cap, no pooling. Bounded by ~10 s of node construction, which evidently stayed under 256 MB.

**Isomorphic move mapping.** Because trees are built on a determinized copy, the chosen `Move` object refers to different `UniqueId`s than the real game state's moves. `MoveComparer.HashMove` hashes moves by `CommandEnum` + `CommonId` (order-insensitive for multi-card choices) and `AreIsomorphic` maps back to a legal official move. Clean solution to a problem every determinizing bot must solve.

### 2.3 Move filtering — the search-space reduction layer

`FilterMoves` runs before every node's move list is stored:

* **Forced instant plays.** If any move plays a card in `InstantPlayCards`, return **only that move**. The list (`InstantPlayCards.cs`) includes `MIDNIGHT_RAID, WAR_SONG, SIEGE_WEAPON_VOLLEY, THE_ARMORY, REINFORCEMENTS, ARCHERS_VOLLEY, LEGIONS_ARRIVAL, THE_PORTCULLIS, FORTIFY, BEWILDERMENT, GRAND_LARCENY, JARRING_LULLABY, POUNCE_AND_PROFIT, SHADOWS_SLUMBER, SWIPE, GOLD, WRIT_OF_COIN`.
* **`COMPLETE_TREASURY`** (Treasury destroy): destroy `BEWILDERMENT` if present, else one `GOLD`, else any cost-0 card, else the first move. A single hard rule replaces search.
* **`DESTROY_CARDS`:** score every single-card choice with `strategy.CardEvaluation`, keep moves whose entire choice set lies within the **3 worst** cards.
* **`REFRESH_CARDS`:** sum `CardEvaluation` over each choice, sort descending, keep the top **10 / 6 / 3** depending on `MaxChoices == 3 / 2 / 1`.

This is a per-choice-type branching cap tuned by hand, and it is materially better engineered than MaltheMCTS's equivalent (which throws on unexpected cases).

### 2.4 Evaluation function (`GameStrategy.Heuristic`)

Output is squashed to `[0, 1]`:

```csharp
const int heuristicMin = -10000, heuristicMax = 10000;
return Math.Clamp(result + heuristicMax, 0.0, 2.0 * heuristicMax) / (2.0 * heuristicMax);
```

with hard terminal shortcuts: `1` if `patronsBonuses == heuristicMax` (4 patrons) or `afterRoundPoint >= 80`; `0` if the enemy is 1 patron from victory or `EnemyPrestige >= 40 && afterRoundPoint <= EnemyPrestige`.

**Game-phase selection:**

```csharp
if (points >= 27 || gameState.EnemyPlayer.Prestige >= 30)  LateGame;
else if (points <= 10 && gameState.EnemyPlayer.Prestige <= 13) EarlyGame;
else MidGame;
```

**The optimized weight vector** (`[Early, Mid, Late]`). The 14-significant-digit values are the output of a numerical optimisation run; the hand-tuned originals survive in a commented block right below:

```csharp
{ Param.Crow,           { 357.16459375015,   401.2613140399906,  439.46242346573    } },
{ Param.Ansei,          {  69.72651456206626, 65.17496269303498,  51.41566943715283 } },
{ Param.Prestige,       {  14.406547468356404, 115.43794731430911, 244.10581096000053 } },
{ Param.CardLimit,      {   3.5835821274533806, 9.802856656572525,  6.1746487022788745 } },
{ Param.ComboPower,     {   1.4531637982059311, 2.017681169347004,  1.7654122025476535 } },
{ Param.OurAgent,       {   2.4073301922708956, 1.204796748703784,  0.6282777779201613 } },
{ Param.EnemyAgent,     { -36.38168831074075, -31.120871807130026, -165.21582096426815 } },
{ Param.UpcomingCard,   {  14.452740668849344, 10.997295351493275,  96.387175942189   } },
{ Param.TierMultiplier, {  32.45573949797829,  15.3624756191444,    17.865162201619203 } },
{ Param.TavernPenatly,  {  -1.0194847580786344, -1.6236002964954,   -1.8425554404914708 } },
{ Param.KnowingCardCombo,{  0.9856322443431382, 1.2677569556414718, 1.8798439140688314 } },
{ Param.After40Bonus,   { 469.6287162071635,  562.8022272879504,  436.4502784121612  } },
```

*(commented-out hand-tuned baseline: Crow 300/500/600, Prestige 10/60/200, CardLimit 13/13/13, ComboPower 3/3/3, EnemyAgent −60/−80/−150, After40Bonus 300/300/300, TierMultiplier 10/10/10.)*

Readings that matter:

* **`Crow` (357 → 439) and `After40Bonus` (≈ 470–560) dominate.** Combined with `PatronTierList`, where Duke of Crows is `favoured = −1, neutral = 0, unfavoured = +1`, the bot receives **−357 to −439 for holding Crows favour** and **+357 to +439 for the opponent holding it**. Independently derived, this matches HQL's `crow_weight = −1000` early. **Two of the top four bots agree that Duke of Crows favour is a liability. That is the strongest strategic consensus in the field.**
* **`Prestige` scales 14.4 → 115.4 → 244.1 (17×).** Deck-building early, prestige racing late. Note `BasicProperties` uses `Prestige + Power` (taunt-adjusted) for our side but bare `Prestige` for the enemy — an intentional "what I score this round" formulation.
* **`EnemyAgent` collapses to −165.2 late** vs −36.4 early — a 4.5× increase in fear of enemy agents in the endgame, applied as `AgentTier * weight * ((hp+2)/maxHp + 2)`.
* **`CardLimit` (3.58 / 9.80 / 6.17) is a deck-thinning knob**, not a card count. It enters as `wspOur = Math.Min(1.0, CardLimit / ourCards.Count())`. With `CardLimit = 3.58` early and a 20-card deck, `wsp ≈ 0.18`, so `CardsValues` becomes roughly *average* card quality — a strong implicit preference for thin, high-quality decks.
* **`ComboPower` 1.45–2.02** gives superlinear reward: `CombosValue = Σ pow(wsp * count_patron, ComboPower)`. Concentrating on two decks beats spreading across four.
* **`TavernPenatly` (−1.02 → −1.84)** subtracts `enemyCardEvaluation(card) * weight` for every card sitting in the tavern — an explicit **denial term**. It values buying a card the opponent wants.
* **`UpcomingCard` explodes to 96.4 late**, applied to `KnownUpcomingDraws.Take(3)` with `HandTierList` values. Late-game the bot cares intensely about what it will draw next turn.

**Patron-win-distance clamps (`PatronsBonuses`):**

```csharp
if (enemyPatronDist == 1) return 3 * heuristicMin;   // -> Heuristic returns 0
if (enemyPatronDist == 2) val = heuristicMin;
if (ourPatrons == 4)      val = heuristicMax;        // -> Heuristic returns 1
```

where `enemyPatronDist` accumulates 2 per patron we hold (1 for Ansei, since Ansei favour is stealable) and 1 per neutral patron. Clean formulation of "how many patron flips until I lose".

**Anti-knockout term.** If `AMBUSH` (weight 2) or `BLACK_SACRAMENT` (weight 1) sits in the tavern, the bot discounts its own best agents' HP contributions by 75% — anticipating that the opponent can buy the removal.

### 2.5 Tier lists

**`GPCardTierList`** (main deck-quality table), phase-indexed, with `S=50, A=30, B=15, WOC=10, C=3, D=1, UNKNOWN=0, CONTRACT_ACTION=0, CURSE=−3`. Note the **non-linear scale — S is 50× D.** Selected entries:

| Card | Early | Mid | Late |
|---|---|---|---|
| Currency Exchange | S | S | A |
| Midnight Raid | S | S | S |
| Plunder | S | S | S |
| Hira's End | S | S | S |
| Blood Sacrifice / Bloody Offering | S | S | S |
| Blackfeather Knave, Toll of Flesh/Silver, Murder of Crows | S | S | A |
| Grand Oratory | B | **S** | **S** |
| Knight Commander | S | S | A |
| Rally / The Armory / Banneret | A | S | A |
| Warrior Wave, Reinforcements | S | A | B |
| Pounce and Profit | S | S | B |
| Ghostscale Sea Serpent | A | C | C |
| Sea Serpent Colossus | A | B | D |
| Storm Shark Wavecaller | C | D | D |
| Sea Elf Raid, Way of the Sword, Swipe, Fortify, War Song, Goods Shipment, Customs Seizure, Mainland Inquiries | D | D | D |
| Bewilderment | CURSE (−3) | CURSE | CURSE |
| **Gold** | UNKNOWN (0) | **CURSE (−3)** | **CURSE (−3)** |
| Writ of Coin | WOC (10) | B (15) | UNKNOWN (0) |
| Alessian Rebel | D | D | D |
| Pelinal Whitestrake / Whitestrake Ascendant | C | A | A |
| Morihaus (both) | B | A | A |
| Chainbreaker Captain / Sergeant | A | A | B |
| All contract actions (Ambush, Barterer, Black Sacrament, Blackmail, Harvest Season, Imprisonment, Ragpicker, Tithe, King Orgnum's Command, Maormer Cutter, Sea Raider's Glory, Augur's Counsel, Sage Counsel) | 0 | 0 | 0 |

Note the **Orgnum deck is rated poorly and decays hard into the late game** (`A→C→C`, `A→B→D`, `C→D→D`, `D→D→D`), which is coherent with BestMCTS3 refusing to draft Orgnum.

**`HandTierList`** (`S=5 … D=1, E=0, CURSE=−1`) rates a card's value *sitting in hand right now*, used only for `KnownUpcomingDraws`. It differs sharply from the deck tier list — e.g. `Hlaalu Councilor/Kinsman/House Embassy = S` in hand but `A/A/C` in deck; **every Orgnum card is `E` (0) in hand**; `Gold` and `Writ of Coin` are `CURSE`. Separating "value in deck" from "value in hand this instant" is a good idea worth stealing.

The file contains the author's own admission that the Psijic and Alessia hand-tier entries were guessed: the block is bracketed by the comments `// Tu troche losuwa poszla xD` ("a bit of randomness went in here lol") and `// Koniec losuwy`.

**`AgentTierList`** (`S=5 … D=1`) — used only to weight *enemy* agents. `HLAALU_COUNCILOR`, `HLAALU_KINSMAN`, `BANNERET`, `KNIGHT_COMMANDER` = S; the entire Alessia and Orgnum agent rosters = D.

**`taunts`** hard-coded set: `STUBBORN_SHADOW, BANNERET, KNIGHT_COMMANDER, SHIELD_BEARER, BANGKORAI_SENTRIES, KNIGHTS_OF_SAINT_PELIN` — subtracted from effective power in both `BasicProperties` and the terminal check.

### 2.6 Patron selection — and a latent hang

```csharp
if (availablePatrons.Contains(PatronId.DUKE_OF_CROWS)) return PatronId.DUKE_OF_CROWS;
if (availablePatrons.Contains(PatronId.RED_EAGLE))     return PatronId.RED_EAGLE;
var patronid = availablePatrons.PickRandom(rngs[0]);
while (patronid == PatronId.ORGNUM || patronid == PatronId.PSIJIC || patronid == PatronId.SAINT_ALESSIA) {
    patronid = availablePatrons.PickRandom(rngs[0]);
}
return patronid;
```

* **Always drafts Duke of Crows.** Combined with `Crow: favoured = −1`, this looks contradictory but is deliberate: it puts Crows *in the game* so the pressure exists, then plays to avoid holding the favour. It is a denial pick.
* **`RED_EAGLE` is not in the 2025 pool** — dead branch.
* **It refuses Orgnum, Psijic, and Saint Alessia entirely.** It never drafts the new deck and never drafts Orgnum, and its tier lists for those decks are self-admittedly guessed. **This is the clearest single regression from its 2023/2024-winning configuration and a large part of why it fell to 4th.**
* **Latent infinite loop.** If every remaining `availablePatrons` entry were in `{ORGNUM, PSIJIC, SAINT_ALESSIA}`, the `while` loop never terminates → `PATRON_SELECTION_TIMEOUT` → instant loss. With the 2025 six-patron pool this cannot trigger (at most two excluded patrons exist, and at least three patrons remain at every pick). **Change the patron pool and this bot hangs.** A 2026 pool containing three or more of Orgnum/Psijic/Alessia would break it outright.

### 2.7 Other notes

* `if (noOfRoots >= 4) cur -= maks + min;` — a trimmed-mean aggregation across roots that is **dead code**, since `maxNoOfRoots = 3`.
* The root-selection loop starts at `val = 0` and only accepts `cur > val`, so if every child scores 0 (a lost position) it silently returns child index 0.
* Comments are in Polish throughout (`chyba niepotrzebne` = "probably unnecessary", `Kara za knockout agent w tawernie` = "penalty for a knockout agent in the tavern").

---

## 3. MaltheMCTS — 11th of 11, 18.62%. Why a real MCTS bot failed

**Files:** `MaltheMCTS.cs` (223), `Node.cs` (540), `ChanceNode.cs` (64), `EndNode.cs` (28), `Settings.cs` (90), `Utility.cs` (294), plus feature-set / hashing / equality extensions (~1100 lines).

On paper this is the most *sophisticated* bot in the field: explicit chance nodes with progressive widening, a cross-move transposition table, per-choice-type branch limits, and a **LightGBM win-probability model** trained on a 21-feature state encoding. It finished last, below two bots that barely search at all. The causes are stacked, and each alone would be damaging.

### 3.1 Cause 1 — The rollout loop is inverted and never executes

```csharp
private SeededGameState RollOutTillEndOfTurn()
{
    var rolloutPossibleMoves = PossibleMoves.ToList();
    var gameState = GameState;

    while (gameState.GameEndState != null && (rolloutPossibleMoves.Count > 1
        || rolloutPossibleMoves[0].Command != CommandEnum.END_TURN))
    { ... }

    return gameState;
}
```

`GameEndState` is **`null` during normal play** and non-null only once the game is over. The condition should be `== null`. As written, **the loop body never runs**: `RollOutTillEndOfTurn` returns the input state unchanged, every time.

The consequence is severe and specific. `HeuristicScoring.Score` is documented as evaluating *end-of-turn* states:

> *"To lower the amount of variables (hand strengths, patron moves available, coins, power, whether agents have been activated) the model needs to process, this model scores states just before ending turn"*

and `FeatureSetUtility.BuildFeatureSet` bakes that assumption in:

```csharp
if (gameState.EnemyPlayer.Agents.All(a => !a.RepresentingCard.Taunt))
{
    //this simplification is becuase this feature set represents end of turn
    currentPlayerPrestige += gameState.CurrentPlayer.Power;
}
```

So the LightGBM model is being queried **entirely off-distribution** — on mid-turn states with unplayed cards still in hand, unspent coins, and un-activated agents, using a feature encoding that pretends the turn is already banked. The model was trained on end-of-turn states and is asked about something else on every single evaluation. **This alone is enough to explain last place.** It makes the search's value signal near-meaningless while still costing full price to compute.

### 3.2 Cause 2 — The evaluation model file is not present, and the path is fragile

```csharp
public static PredictionEngine<GameStateFeatureSetCsvRow, ModelOutput>? GetPredictionEngine()
{
    string basePath = "MaltheMCTS/Ensemble_Tree_Models/";
    var lightGbmModel = mlContext.Model.Load(basePath + "LightGbm", out var _);
    return mlContext.Model.CreatePredictionEngine<...>(lightGbmModel);
}
```

The path is **relative**, so it resolves against the *process working directory*, not the assembly directory. Compare HQL, which does this correctly:

```csharp
private static readonly string BaseDir = AppContext.BaseDirectory;
private static readonly string BotsDir = Path.Combine(BaseDir, "Bots");
public static readonly string qTable1Path = Path.Combine(BotsDir, "QTABLE1.txt");
```

Furthermore, **no `Ensemble_Tree_Models` directory or `LightGbm` file exists anywhere in the archive**, including inside `TournamentRunnerEnv.tar.xz` (verified by full tar listing). Whether the organisers received the model with the original submission is unknown, but the archived, reproducible configuration cannot load it.

This is called from **both constructors, outside any try/catch**. If the load throws, the agent cannot be constructed. If it somehow returns `null`, `HeuristicScoring` silently falls back to `SimpleManualEvaluation`, which returns an **unbounded signed score**, not a probability — while `Node.Visit` and `GetUCBScore` assume `[0,1]` and `Score()`'s early-return branch is skipped entirely for the null case:

```csharp
// The manual model (null) does not return either 0 and 1 or -1 and 1, so this logic does not apply for it
if (predictionEngine != null) { ...return 1 / return 0... }
```

Either way — model present and off-distribution, or model absent and the fallback breaking UCB's scale assumption — the value function is broken.

### 3.3 Cause 3 — Everything is wrapped in a bare catch that plays randomly

```csharp
public override Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
{
    try { ... }
    catch
    {
        // Something went wrong while trying to compute move. Playing random move instead.
        return possibleMoves.PickRandom(new SeededRandom());
    }
}
```

This is a defensible choice against `BOT_EXCEPTION` losses, but it converts every bug into **silent random play** with no logging and no signal to the author. And the code has many throw sites reachable in normal play:

* `FilterMoves` — `throw new NotImplementedException("Unexpected choice follow up: " + ...)` on any `START_OF_TURN_CHOICE_PENDING` that is not `DISCARD_CARDS`.
* `Expand` — `throw new Exception("Expand was unexpectedly called on a node that was fully expanded")`.
* `MiscellaneousExtensions.IsStochastic` / `IsInstantPlay` — `throw new Exception("Unknown move type: ...")` on any unrecognised move or effect type.
* `Utility.GetRankedCardCombinationMoves` — repeated `moves.First(...)` calls that throw `InvalidOperationException` when the assumed combination is absent (and it computes `emptyMove` in `case 3` without ever adding it — a dead assignment).
* `FeatureSetUtility.GetPatronRatios` — `patronToAmount[currCard.Deck] += 1` throws `KeyNotFoundException` if a card's deck is not in `gameState.Patrons`.

Also note `new SeededRandom()` is constructed fresh on every fallback, so the "random" move is drawn from a freshly seeded generator each time — likely heavily biased toward the same index.

`Utility.CategorizeCards()` is called **only in the parameterless constructor**. The `(string?, Settings?)` overload never calls it, leaving `INSTANT_EFFECT_PLAY_CARDS` and `RANDOM_EFFECT_CARDS` empty — instant-play detection and chance-node detection both silently disabled.

### 3.4 Cause 4 — The transposition table is both a correctness bug and a memory leak

```csharp
public Dictionary<int, List<Node>> NodeGameStateHashMap = new Dictionary<int, List<Node>>();
public override void PregamePrepare() { NodeGameStateHashMap = new Dictionary<int, List<Node>>(); }
```

**The map is cleared once per game, never per turn or per move.** Every node ever created — each holding a full `SeededGameState` (both players' hands, draw piles, cooldown piles, played piles, agents, tavern, effects) — is retained for the entire game. With `REUSE_TREE = true` and hundreds of turns at ~10 s of node construction each, this grows monotonically. **This is the 256 MB risk**, and it is the structural opposite of BestMCTS3's discard-at-turn-boundary policy.

Worse, the identity test is wrong in three places:

```csharp
&&  instance.EnemyPlayer.PatronCalls == instance.EnemyPlayer.PatronCalls   // self-comparison
&&  instance.EnemyPlayer.Power       == instance.EnemyPlayer.Power         // self-comparison
&&  instance.EnemyPlayer.Prestige    == instance.EnemyPlayer.Prestige      // self-comparison
```

Three `instance == instance` comparisons that are always `true`. **States differing only in the opponent's power, prestige, or patron calls are treated as identical** and share a node — so scores from a position where the opponent is at 10 prestige get reused in a position where they are at 70.

And the sort-then-compare list helpers discard their results:

```csharp
instance.OrderBy(effect => effect.Amount).ThenBy(...);   // return value thrown away
other.OrderBy(effect => effect.Amount).ThenBy(...);      // LINQ OrderBy is not in-place
```

`OrderBy` is lazy and non-mutating; both lines are no-ops. The subsequent index-wise comparison runs on the original, unsorted order — the opposite of what the code intends.

Finally, the collision path:

```csharp
try { equalNode = bot.NodeGameStateHashMap[hash].SingleOrDefault(node => node.GameState.IsIdentical(result.GameState)); }
catch (Exception e) { /* builds an error string, then discards it */ }
if (equalNode != null) result = equalNode;
else bot.NodeGameStateHashMap[hash].Add(result);
```

`SingleOrDefault` **throws** when two nodes match; the catch swallows it, leaves `equalNode == null`, and the code then **appends yet another duplicate** to the bucket — making the next collision more likely. The buckets grow, and `IsIdentical` (a full deep comparison of both decks, tavern, and effects) is run against every member on every lookup. Node lookup degrades toward O(n · deck size).

The hash itself compounds this. `GenerateHash` multiplies `int`s in unchecked arithmetic (`drawPileHash *= ((int)currCard.CommonId + 1) * 1000` over a 20-card pile overflows immediately) and is order-insensitive by construction, while `PendingChoiceHash` uses `currCard.UniqueId` — **instance identity**, which differs between logically identical determinized states. So the hash is simultaneously too collision-prone and too discriminating in the one place it should not be.

### 3.5 Cause 5 — Time management is optimistic and unbounded per iteration

```csharp
int estimatedRemainingMovesInTurn = EstimateRemainingMovesInTurn(gameState, possibleMoves);
double millisecondsForMove = (remainingTime.TotalMilliseconds / estimatedRemainingMovesInTurn)
                             - Settings.ITERATION_COMPLETION_MILLISECONDS_BUFFER;   // buffer = 50
while (moveTimer.ElapsedMilliseconds < millisecondsForMove) {
    rootNode.Visit(out double score, new HashSet<Node>());
}
```

The division by an estimated move count is a reasonable idea (BestMCTS3 does the same thing better), but:

* **`EstimateRemainingMovesInTurn` is itself expensive.** It performs a full simulated playout of the remaining turn — repeated `ApplyMove` calls plus `FindInstantPlayMove` scans — *before* the search starts, and the cost is unbounded.
* **The loop condition is checked only between iterations.** A single `Visit` recurses to a leaf, may construct several `Node`s (each computing `GenerateHash` over the entire state and running `IsIdentical` against a growing bucket), and ends with a LightGBM `PredictionEngine.Predict` call. There is **no interruption**, so the overshoot is bounded only by how long one iteration takes — and iteration cost *grows over the game* as the transposition table fills.
* **The safety margin is 50 ms** against a hard-loss timeout. BestMCTS3 uses a 100 ms turn margin *plus* a 0.5 s per-move cap *plus* a random-move panic escape below 0.1 s. Sakkirina uses a 200 ms turn margin plus a greedy fallback. MaltheMCTS has 50 ms and no escape hatch.
* `new HashSet<Node>()` is allocated per iteration and grows to the depth of the descent — per-iteration allocation churn on top of everything else.

### 3.6 Cause 6 — Sign convention inconsistency in backup

```csharp
selectedEdge.Child.Visit(out score, visitedNodes);
if (selectedEdge.Child.GameState.CurrentPlayer.PlayerID != playerId) {
    score *= -1; // this assumes the score is representing a winrate in a zero-sum-game format
}
```

The comment is exactly the problem: `HeuristicScoring.Score` returns a **win probability in `[0,1]`**, not a zero-sum score in `[−1,1]`. Negating a probability produces `[−1,0]`. Consequently **every node where the turn passes to the opponent scores strictly below every node where we keep the turn**, regardless of how good or bad the position is. Correct handling is `1 − score`. Combined with `FORCE_DELAY_TURN_END_IN_ROLLOUT = true`, the bot has a systematic bias against ever ending its turn. Meanwhile `EndNode.Visit` returns an unnegated `[0,1]` score, so the tree mixes three incompatible value scales.

`GetUCBScore` under `UPDATED_TREE_REUSE = true` also reduces to an identity:

```csharp
var simulatedTotalScore = (edge.Child.TotalScore / edge.Child.VisitCount) * edge.VisitCount;
double exploitation = simulatedTotalScore / edge.VisitCount;   // == Child.TotalScore / Child.VisitCount
```

— two multiplications and a division that cancel exactly, i.e. the "updated tree reuse" feature does nothing at all.

### 3.7 Cause 7 — Aggressive move filtering that is strategically wrong

```csharp
case BoardState.NORMAL:
    // Limit to play all cards before buying from tavern or activating patrons
    bool canPlayCards = GameState.CurrentPlayer.Hand.Count > 0;
    if (canPlayCards) {
        PossibleMoves = PossibleMoves.Where(m => m.Command == CommandEnum.PLAY_CARD).ToList();
    }
```

**While any card remains in hand, buying, patron-calling, and agent activation are all removed from the move list.** This is a plausible-sounding heuristic that is simply false in Tales of Tribute: buying a card mid-hand to hit a combo threshold, calling Treasury to destroy a card you just played, or activating an agent before playing a card that scales off board state are all standard lines. The search is forbidden from finding any of them.

`ChoiceFollowUp.REPLACE_CARDS_IN_TAVERN` is handled by picking `CHOICE_BRANCH_LIMIT` **uniformly random moves** — and the `while (indexes.Count < limit)` loop will **hang forever** if `PossibleMoves.Count < CHOICE_BRANCH_LIMIT` (5), because the `HashSet<int>` can never reach 5 distinct indices. That is a turn timeout, i.e. an instant loss. (The branch is guarded by `PossibleMoves.Count > CHOICE_BRANCH_LIMIT`, so it is unreachable as written — but it is one refactor away from being fatal.)

### 3.8 Patron selection

```csharp
if (availablePatrons.Contains(PatronId.HLAALU))        return PatronId.HLAALU;
if (availablePatrons.Contains(PatronId.DUKE_OF_CROWS)) return PatronId.DUKE_OF_CROWS;
if (availablePatrons.Contains(PatronId.ANSEI))         return PatronId.ANSEI;
if (availablePatrons.Contains(PatronId.RAJHIN))        return PatronId.RAJHIN;
return availablePatrons.PickRandom(new SeededRandom());
```

`HLAALU` is not in the 2025 pool — dead first branch, same legacy-configuration failure as HQL's `Count == 8` and BestMCTS3's `RED_EAGLE`. **All three bots carry dead patron-preference code from an older patron pool.** MaltheMCTS then takes Duke of Crows first, contradicting the consensus of the two bots above it that Crows favour is a liability — and it has no heuristic term anywhere that expresses Crows' cost.

### 3.9 Verdict: it was not timeouts or memory alone

The round-by-round data settles this. MaltheMCTS scored 22.5% in round 1 and *drifted down* to 18.4% — the shape of a bot that is **structurally weak from move one**, with a mild additional degradation consistent with a growing transposition table (slower iterations, more memory pressure, more fallback-to-random). If timeouts or OOM were the primary cause, we would expect a sharp cliff or a much steeper decline.

The dominant cause is **the evaluation function**: an off-distribution model query (the inverted rollout guard) feeding a value scale that UCB misinterprets (the sign bug), on top of a value model that may not have loaded at all. Everything else — the leaked transposition table, the wrong identity test, the no-op sorts, the 50 ms margin, the bare catch — turns a losing bot into a bot that loses to *everyone*, including two bots at 21% and 23%.

**The general lesson: MCTS amplifies your evaluation function. If the evaluation is wrong, more search makes the bot worse, not better, because it optimises harder against the wrong objective.** HQL's crude 12-term integer heuristic with a half-empty value table beat a LightGBM model because HQL's heuristic is *approximately right and consistently applied*, and MaltheMCTS's is not.

---

## 4. Head-to-head matrix: matchup structure and rock-paper-scissors effects

Source: `head_to_head.csv`. Cell `(row, col)` = the row bot's win rate against the column bot.

### 4.1 The headline result — HQL crushes BestMCTS3

| | vs BestMCTS3 |
|---|---:|
| **HQL_Plus_BOT** | **0.771** |

BestMCTS3 wins only **22.9%** against HQL — its worst matchup by a wide margin, and the single most lopsided result among the top four. Meanwhile:

| | vs HQL | vs BestMCTS3 |
|---|---:|---:|
| SakkirinaSolo | 0.474 | 0.614 |
| Sakkirina | 0.465 | 0.582 |
| EBot_C | 0.514 | 0.128 |
| NAgent | 0.498 | **0.705** |

**HQL beats both Sakkirinas head-to-head (0.535 and 0.526) yet finishes below them.** The Sakkirinas earn their rank by farming the mid-field: 0.72–0.79 against EBot_C/EBot_F where HQL only manages 0.486/0.627, and ~0.95–0.97 against the bottom three where HQL manages 0.857–0.963. **The 2025 ranking was decided by mid-field farming, not by the top-of-table matchups.** This is a direct, actionable insight for 2026: a bot optimised only against the strongest opponents can win every top matchup and still lose the tournament.

### 4.2 Rock-paper-scissors structure

16 of 165 triples (9.7%) form a strict 3-cycle. The important ones:

* **`BestMCTS3 → EBot_C → HQL_Plus_BOT → BestMCTS3`** (0.87 / 0.51 / 0.77) — the cycle that decided 3rd vs 4th place.
* **`NAgent → BestMCTS3`** at **0.705**, while `NAgent` is 8th at 50.4% and `BestMCTS3` is 4th at 66.1%. The largest rank-inversion in the field.
* **`BestMCTS3 → Council Of Two → NAgent → BestMCTS3`** (0.92 / 0.64 / 0.70) — `Council Of Two` is 9th at 22.9% and beats an average bot 64% of the time.
* **`NAgent → SakkirinaSolo`** at **0.563** — the 8th-place bot beats the champion.
* **`EBot_C → HQL_Plus_BOT`** at 0.514 — the 5th-place bot is HQL's *only* losing matchup.
* **`MaltheMCTS → ZMyBot → NAgent → MaltheMCTS`** (0.62 / 0.56 / 0.98) — even the bottom of the table cycles.

### 4.3 Consistency profile — the key discriminator

| Bot | mean WR | **std dev** | worst matchup | best |
|---|---:|---:|---|---:|
| SakkirinaSolo | 0.716 | **0.184** | NAgent 0.437 | 0.970 |
| Sakkirina | 0.705 | **0.187** | SakkirinaSolo 0.430 | 0.972 |
| HQL_Plus_BOT | 0.692 | **0.176** | EBot_C 0.486 | 0.963 |
| BestMCTS3 | 0.661 | **0.280** | HQL_Plus_BOT 0.229 | 0.957 |
| EBot_C | 0.562 | 0.259 | BestMCTS3 0.128 | 0.889 |
| EBot_F | 0.520 | 0.264 | BestMCTS3 0.132 | 0.882 |
| MCTS_MMHVR | 0.512 | 0.212 | BestMCTS3 0.293 | 0.865 |
| NAgent | 0.503 | 0.193 | EBot_C 0.313 | 0.975 |
| MaltheMCTS | 0.185 | 0.204 | NAgent 0.025 | 0.620 |

**The top three all have σ ≈ 0.18; BestMCTS3 has σ = 0.28.** HQL's floor across all ten opponents is 0.486 — it never has a genuinely bad matchup. BestMCTS3 has a 0.229 hole. **The three bots above BestMCTS3 are not better at their best; they are better at their worst.** In an all-play-all tournament, minimising your worst matchup is worth more than maximising your best.

### 4.4 What the matchups tell us about play styles

* **BestMCTS3 is highly polarised**: it beats every rule-based / shallow bot by 0.87–0.96 (EBot_C 0.872, EBot_F 0.868, ZMyBot 0.957, Council 0.920, MaltheMCTS 0.957) and loses badly to HQL (0.229) and NAgent (0.295). Its 0.5 s/move max-backup MCTS overwhelms opponents that do not search, and gets punished by opponents whose strategic priors differ from its tier list.
* The **HQL → BestMCTS3** result is most plausibly a **patron-draft and deck-preference clash**: HQL preferentially drafts Orgnum and Rajhin; BestMCTS3 *refuses* to draft Orgnum and Alessia and rates Orgnum cards `A→C→C`/`A→B→D`/`C→D→D`. When HQL takes Orgnum, BestMCTS3 is left evaluating an Orgnum-heavy board with a tier list that considers Orgnum near-worthless, while HQL has learned Q-values for `SEA_SERPENT_COLOSSUS (17.3)`, `PYANDONEAN_WAR_FLEET (16.9)`, `SERPENTGUARD_RIDER (16.9)`, and `SUMMERSET_SACKING (16.9)`. **BestMCTS3 is out-of-distribution on exactly the patron HQL prefers.**
* **NAgent's 0.705 against BestMCTS3** with a 50% overall rate is the same pattern from the other side — a specific style that BestMCTS3's evaluation mis-scores.
* **MaltheMCTS's floor of 0.025 against NAgent** (and 0.028/0.030 against the Sakkirinas) is not a matchup, it is a broken bot. Its 0.535 against Council Of Two and 0.620 against ZMyBot suggest those two are near-random themselves.

---

## 5. Synthesis: what separates the tiers

### ~70% (Sakkirina family, HQL) vs ~66% (BestMCTS3)

| | ~70% bots | BestMCTS3 (66%) |
|---|---|---|
| **Worst matchup** | 0.44–0.49 | **0.229** |
| **Std dev across opponents** | 0.176–0.187 | **0.280** |
| **Patron coverage** | HQL drafts Orgnum; Sakkirina drafts uniformly at random (all patrons in-distribution) | **Refuses Orgnum, Psijic, Alessia** |
| **Tier-list currency** | HQL's Q-table is data-derived, though 66/125 entries are zero; Sakkirina's is hand-tuned but covers all decks | Alessia/Psijic hand-tier entries **self-admittedly random** |
| **Rule layer** | Large — HQL does zero search in the play phase; Sakkirina's `resourceOnlyCards`/`resourceOnlyAgents` bypass search for most moves | Moderate — `InstantPlayCards` + per-choice branch caps |
| **Time safety margin** | Sakkirina 200 ms turn + 0.65 s/move cap + greedy fallback; HQL structurally cannot overrun | 100 ms turn + 0.5 s/move cap + random-move panic below 0.1 s |
| **Backup rule** | max (Sakkirina); beam-max (HQL) | max |

Three things separate them:

1. **Robustness beats peak strength.** The variance table is the whole story. BestMCTS3's search quality is arguably the best in the field — it beats the weak bots harder than anyone. It has one 23% hole and that hole cost it two places.
2. **Currency with the current patron pool.** BestMCTS3 is a 2023/2024 champion running 2023/2024 priors: dead `RED_EAGLE` branch, refusal to draft the two newest patrons, guessed tier values for the Alessia deck, and a latent infinite loop if the pool composition changes. **Its regression is a maintenance failure, not an algorithmic one.**
3. **A thick rule layer that bypasses search.** All three ~70% bots handle the majority of decisions with hard-coded rules and spend their search budget on the few decisions that matter (purchases, patron calls, destroy/discard choices). BestMCTS3 searches more of the move space, and against a strong static opponent that extra search is not worth what it costs in per-decision time.

Notably, **SakkirinaSolo (71.8%) is the *simpler* of the two Sakkirinas** — it searches within a single turn, while Sakkirina (70.6%) does a two-ply search across the turn boundary at 1.0 s/move instead of 0.65 s. The extra opponent modelling cost 1.2 points.

### ~66% vs 18.6%

Not a matter of degree. BestMCTS3 and MaltheMCTS use the same family of algorithm and the same broad structure (determinized MCTS, tier/feature evaluation, move filtering, tree reuse). The gap is entirely execution:

| | BestMCTS3 | MaltheMCTS |
|---|---|---|
| Evaluation queried in-distribution | yes | **no** — inverted rollout guard |
| Value scale coherent with UCB | yes, `[0,1]` throughout | **no** — mixes `[0,1]`, `[−1,0]`, unbounded |
| Evaluation artifact present | tier lists are source code | **model file absent from the archive**, relative path |
| Tree lifetime | discarded at turn boundary | **retained for the entire game** |
| State identity test | multiset compare, correct | **3 self-comparison bugs, 3 no-op sorts** |
| Time margin | 100 ms + 0.5 s cap + panic escape | **50 ms, no escape** |
| Failure mode | plays a random *move* below 0.1 s | **plays random on every exception, silently** |
| Dead/legacy code | `RED_EAGLE` branch, dead trimmed-mean | `HLAALU` branch, `UPDATED_TREE_REUSE` is a no-op, `CategorizeCards` skipped on one ctor |

---

## 6. Recommendations for the 2026 bot

### Borrow (as inspiration, reimplemented)

1. **Max backup within the turn.** Both HQL's beam and BestMCTS3's and SakkirinaSolo's MCTS treat the turn as a single-agent planning problem and back up the maximum. This is correct for Tales of Tribute and is the single highest-leverage algorithmic choice.
2. **Root determinization ensembles.** HQL's 7 independent beam searches + plurality vote and BestMCTS3's 3 fixed-seed roots both attack hidden information without a belief model. Cheap, effective, easy to tune.
3. **Self-measuring time budgets.** BestMCTS3's probe-then-divide (`20 iterations → maxNoOfMoves → remaining / (maxNoOfMoves + 1)`, capped at 0.5 s) is better than a fixed per-move slice because turn lengths vary enormously. Benchmark it against Sakkirina's simpler `min(0.65 s, remaining / 4)`.
4. **A degradation ladder with a real escape hatch.** Cap per-move time; drop root count when the turn budget runs low; fall back to a *greedy heuristic* move (Sakkirina) rather than a random one (BestMCTS3) below the floor; keep a ≥200 ms turn margin.
5. **Discard the tree at every turn boundary.** BestMCTS3 and Sakkirina both do this, MaltheMCTS does not, and it is the clearest memory-safety rule in the data.
6. **Validate tree reuse against reality.** `CheckIfSameGameStateAfterOneMove` (multiset compare of hand / tavern / cooldown / draw pile) before descending into a reused subtree is cheap insurance against determinization drift.
7. **The solved-subtree flag.** BestMCTS3's `full` propagation with `UCBScore → double.MinValue` lets the search terminate early on small turns and reallocate time. Nobody else does this and it is free.
8. **Separate "value in deck" from "value in hand".** BestMCTS3's `GPCardTierList` vs `HandTierList` split is a real insight — Hlaalu agents are S in hand and A/C in deck; every Orgnum card is E in hand. Extend to a third table for "value in tavern to deny the opponent" (BestMCTS3's `TavernPenatly`).
9. **A thick rule layer.** All three top bots bypass search for the majority of moves. Build a well-tested instant-play whitelist first; it multiplies the effective depth of whatever search you run.
10. **Superlinear combo rewards and deck-size normalisation.** `pow(wsp * count, ComboPower)` with `ComboPower ≈ 1.45–2.02`, and `wsp = min(1, CardLimit / deckSize)` with `CardLimit ≈ 3.6–9.8`, is a compact way to express "thin, concentrated decks win".
11. **Numerically optimise the weight vector.** BestMCTS3's 14-digit constants are the product of an optimisation run, and its commented-out hand-tuned baseline (Crow 300/500/600 → 357/401/439; Prestige 10/60/200 → 14.4/115.4/244.1) shows the optimiser materially moved them. Budget time for this.
12. **The Duke of Crows consensus.** Independently, HQL (`crow_weight = −1000` early, `+20` late; hard rule never to call Crows before Late with <7 coins), BestMCTS3 (`Crow` weight 357–439 with `favoured = −1`), and Sakkirina (`±100`, sign inverted on own favour; `logit = Coins − 5 + progress × 2.5`) all conclude that Crows favour is a liability that becomes an asset late with a big coin bank. Encode this.

### Exploit (2025 opponents, if they return)

* **HQL is blind to the opponent's deck quality.** Its heuristic scores enemy cards only through a combo term that is itself computed from *our* played and cooldown piles. Build a strong, quiet engine; HQL will not react until the prestige lands.
* **HQL does no search in the action-play phase.** It plays the first whitelisted card, or literally the first move in the list. Any line that requires a non-obvious play ordering is beyond it.
* **HQL treats all contract actions and all Treasury cards as equal (Q = 0 → floor 1).** Contract-action-heavy lines are invisible to its buy evaluation.
* **HQL is undertrained on Saint Alessia** (`PELINAL_WHITESTRAKE` has 2 of 20 states, `AYLEID_*` absent entirely). Draft Alessia against it.
* **BestMCTS3 never drafts Orgnum, Psijic, or Saint Alessia** and rates those decks near-worthless. Take Orgnum or Alessia and it is evaluating off-distribution — very likely the mechanism behind HQL's 0.771.
* **BestMCTS3's `SelectPatron` will hang** if every remaining available patron is in `{ORGNUM, PSIJIC, SAINT_ALESSIA}`. Safe with the 2025 six-patron pool; **not safe if the 2026 pool changes.**
* **BestMCTS3 plays a uniformly random move** whenever its computed per-move budget drops below 0.1 s. Long, branchy turns push it into that regime.

### Avoid

* **Never let a learned evaluator be queried off the distribution it was trained on.** MaltheMCTS's model was trained on end-of-turn states and asked about mid-turn states on every call, because of a single inverted null check. Assert the invariant in code.
* **Never mix value scales.** Pick `[0,1]` win probability *or* zero-sum `[−1,1]` and enforce it at every backup site. `1 − score`, not `−score`.
* **Never ship a bare `catch` that silently plays randomly.** Log it, count it, and fail loudly in testing.
* **Never use relative paths for data files.** Use `AppContext.BaseDirectory` (as HQL does) and verify the artifact is actually in the submission archive.
* **Never hold a transposition table across turns without a cap or eviction policy**, and audit `IsIdentical`-style equality methods line by line — MaltheMCTS had three `x == x` self-comparisons and three no-op `OrderBy` calls that no test would catch.
* **Prune legacy patron branches every year.** All three bots carry dead code for patrons not in the pool (`Count == 8`, `RED_EAGLE`, `HLAALU`).
* **Do not over-invest in opponent modelling across the turn boundary.** SakkirinaSolo's single-turn search beat Sakkirina's two-ply search by 1.2 points at less than two-thirds the per-move time.
* **Do not optimise only against the top of the table.** HQL beat both bots that finished above it and still placed third. The mid-field is where the ranking was decided.
