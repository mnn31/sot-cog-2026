# Sakkirina / SakkirinaSolo — Full Technical Teardown

Targets: 1st and 2nd place, **2025 Tales of Tribute AI Competition (IEEE COG 2025)**.

| Bot | Place | Field WR | Games |
|---|---|---|---|
| `SakkirinaSolo` | 1st | **71.84 %** | 5440 |
| `Sakkirina` | 2nd | **70.61 %** | 5440 |
| `HQL_Plus_BOT` | 3rd | 69.21 % | 5440 |
| `BestMCTS3` | 4th | 66.07 % | 5440 |

**Sources read in full** (all under `ScriptsOfTribute-CompetitionsArchive/competition-2025-08-COG/cs_agents/`):
`Sakkirina/Sakkirina.cs` (1027 lines), `Sakkirina/CardTierList.cs` (178), `Sakkirina/Extensions.cs` (15), `SakkirinaSolo/SakkirinaSolo.cs` (996), plus `SakkirinaSolo/CardTierList.cs` and `SakkirinaSolo/Extensions.cs` — both **byte-identical** to Sakkirina's.

**Supporting:** `ScriptsOfTribute-Core/Engine/src/AI/ScriptsOfTributeGame.cs` (turn clock), `.../AI/ScriptsOfTribute.cs` (patron draft), `.../Serializers/SeededGameState.cs` (determinization), `.../Board/BoardManager.cs` (win conditions), and `competition-2025-08-COG/{ranking.csv, head_to_head.csv, README.md, TournamentRunnerEnv.tar.xz:config.yaml}`.

Code comments are Japanese; translations are the analyst's.

---

## 0. Executive summary

Sakkirina is **not textbook MCTS** despite looking like it. It is a **near-deterministic best-first search with max-backup**, driven by a hand-written move-ordering policy and a hand-tuned linear evaluation, run over a **single determinization**, with heavy rule-based short-circuiting.

Strength comes from, in order: (1) the rule base, which answers most moves instantly so the budget concentrates on hard decisions; (2) the hand-written rollout policy, which at temperature 1 over logits of magnitude 20–10000 is *effectively greedy* — low variance, high bias; (3) a card-tier evaluation that is ~95 % "sum of card tiers ÷ √(deck size)".

Weakness is the mirror image: the eval is a coarse 5-bucket card-quality greed function with **almost no synergy, combo, tempo, or coins/power modelling**; there is **one determinization per turn**; and the highest-frequency code path force-plays "resource-only" cards **in uniformly random order**, silently randomizing combo sequencing.

`SakkirinaSolo` is `Sakkirina` **with the opponent-turn lookahead deleted** and the per-move slice cut 1.00 s → 0.65 s. Removing the opponent model made it *better* by 1.2 pp.

---

## 1. Overall architecture

### 1.1 Class layout

```
Sakkirina : AI
├── class Child            // an edge (move) out of a Node; owns a set of chance-outcome Nodes
├── class Node             // a game state + its list of Child edges
├── static Evaluate(...)   // leaf evaluation → (0,1) via sigmoid
├── static SimulationPolicy(...) // move-ordering logits (also used as MCTS prior)
├── static RootRuleBasedMove(...) // hard-coded instant answers
├── TreeSearch(...)        // one search iteration
└── Play(...)              // entry point; per-move time slice
```

`Node` = decision point (max node). `Child` = chosen move, owning a **list of `Node`s** for different stochastic outcomes — an explicit **chance-node layer**.

### 1.2 Search algorithm

`TreeSearch` (SakkirinaSolo:801–818):

```csharp
double TreeSearch(Node node, SeededRandom rng, int depth)
{
    var child = node.BanditChild(depth == 0);
    double value;
    if (child.move.Command == CommandEnum.END_TURN) {
        value = child.wins <= 0 ? MoveSimulate(node.gameState, child.move, rng) : child.wins;
    } else if (child.visits < 1 || depth > 20) {
        value = MoveSimulate(node.gameState, child.move, rng);
    } else {
        var next = child.SelectChance(rng);
        value = TreeSearch(next, rng, depth + 1);
    }
    child.Update(value);
    node.Update(value);
    return value;
}
```

- **Selection**: UCT variant (§1.3).
- **Expansion**: a child becomes a real `Node` only on its *second* visit.
- **Rollout**: `MoveSimulate` → `Simulate` plays the rest of the turn greedily, then `Evaluate`. A **truncated rollout to end-of-turn**, not end-of-game.
- **Backup**: the unusual part —

```csharp
// Node.Update — MAX backup, not average
public void Update(double v)
{
    double wins = 0;
    foreach (var child in this.childs) wins = Math.Max(wins, child.wins);
    this.wins = wins;
    this.visits += 1;
}
```

All nodes within a turn belong to the same player (no minimax alternation), so max-backup is the *correct* operator — but it is optimistic, making this behave like **best-first / AO\*** rather than statistical MCTS.

`Child.Update` blends its own rollout average with its chance-children:

```csharp
public void Update(double v)
{
    this.avgWins = (this.avgWins * this.visits + v) / (this.visits + 1);
    if (this.nodes is null) { this.wins = this.avgWins; }
    else {
        double wins = 0; ulong visits = 0;
        foreach (var node in this.nodes) { wins += node.wins * node.visits; visits += node.visits; }
        this.wins = (wins + this.avgWins * (this.visits + 1 - visits)) / (this.visits + 1);
    }
    this.visits += 1;
}
```

i.e. a **partially-expanded expectation with a rollout-based residual** for the unexpanded probability mass. Genuinely elegant.

### 1.3 Bandit formula

```csharp
public Child BanditChild(bool root)
{
    double logVisits = Math.Log(this.visits + 1);
    double v = (this.wins * this.visits + 0.5) / (this.visits + 1);   // shrink toward 0.5
    foreach (var child in this.childs) {
        double q = (child.wins * child.visits + v) / (child.visits + 1);  // shrink toward parent
        double p = 0.9 + 0.1 * child.prob;
        double score = q + (root ? 3 : 1) * Math.Sqrt(2 * p * logVisits / (child.visits + 1));
        ...
    }
}
```

- `q` is shrunk toward the parent value with weight 1 (one-pseudo-count prior); unvisited children start at the parent's value — a first-play-urgency scheme.
- Exploration constant is **3 at the root, 1 elsewhere**.
- **`p = 0.9 + 0.1·child.prob` is nearly a bug.** `prob ∈ [0,1]` → `p ∈ [0.9,1.0]` → `√p ∈ [0.949,1.0]`. The hand-built policy prior modulates exploration by **at most 5.4 %**. The author built a PUCT prior then neutered it; its real influence is via the *rollout* policy only. **Net: the tree policy is plain UCT with C≈1 (C=3 at root).**

### 1.4 Final move selection

```csharp
public Child BestChild()
{
    double bestScore = -100000; int selected = 0; int index = 0;
    foreach (var child in this.childs) {
        double score = child.wins;                        // Solo
        // double score = (child.wins0 + child.wins1) / 2;   // non-Solo
        if (score > bestScore) { bestScore = score; selected = index; }
        index++;
    }
    return this.childs[selected];
}
```

**Max-value, not max-visits.** Strict `>` means **ties resolve to the first child in list order** — and that order is the shuffled `possibleMoves` order. Ties are common (§6.4).

### 1.5 Chance nodes and progressive widening

```csharp
public Node SelectChance(SeededRandom rng)
{
    if (this.nodes is null) { this.nodes = new List<Node>(); this.AddNode(0); return this.nodes[0]; }
    if (!this.stochastic) return this.nodes[0];

    if (this.selected == 0) {
        double k = Math.Pow(visits, 0.3);
        if (k >= this.nodes.Count) {
            if (this.parent.childSeeds is null) this.parent.childSeeds = new List<ulong>();
            ulong seed;
            if (this.parent.childSeeds.Count >= this.nodes.Count) seed = this.parent.childSeeds[this.nodes.Count - 1];
            else { seed = (ulong)rng.Next(); this.parent.childSeeds.Add(seed); }
            this.AddNode(seed);
            this.selected = this.nodes.Count - 1;
            return this.nodes.Last();
        }
    }
    this.selected -= 1;
    if (this.selected < 0) this.selected = this.nodes.Count - 1;
    return this.nodes[this.selected];
}
```

Four good ideas:

1. **Stochasticity detection.** `CheckRandomTransition` compares post-move hand and tavern against pre-move hand + `KnownUpcomingDraws` + tavern. Deterministic moves get exactly **one** child node and are never re-sampled — a large saving, since most ToT moves are deterministic.
2. **Progressive widening** at `k = visits^0.3` (1000 visits → 8 outcomes; 100000 → 32).
3. **Common random numbers.** `parent.childSeeds` is *shared across sibling moves*, so move A's 3rd outcome and move B's 3rd outcome use the same seed. Textbook paired-sampling variance reduction — the most sophisticated thing in the codebase.
4. Outcome selection is **round-robin**, not random — deterministic uniform coverage.

### 1.6 Determinization / hidden information

```csharp
var sgs = gameState.ToSeededGameState((ulong)rng.Next());
```

**Exactly one determinization**, drawn per `Play()` call — but when the tree is reused within a turn (§1.8), `sgs` is **discarded** and the tree keeps its original world. In practice: **one sampled world per turn.**

The engine's `SeededGameState` ctor: own cooldown shuffled; own draw shuffled *except* the first `KnownUpcomingDraws.Count` cards; **enemy hand and draw pile concatenated, shuffled together, re-split**; tavern draw shuffled.

There is **no root ensemble, no particle filter, no belief update from observed opponent plays**. PIMC with N = 1 — the weakest possible configuration, directly vulnerable to strategy fusion and non-locality.

### 1.7 Learned / tuned parameters

**None are learned.** Everything hand-tuned: 124 hand-assigned card tiers; `TierEnum` values (note the commented-out alternative scale `S=1000, A=400, B=200, C=90, D=40` — the author experimented with the gradient); all `Evaluate` weights; all `SimulationPolicy` logits; six hand-curated `HashSet<CardId>` categories. No net, no Q-table, no self-play output. Compare `HQL_Plus_BOT`, which ships two pretrained Q-tables of ~2390 entries each.

### 1.8 Memory strategy / tree reuse

```csharp
if (gameState.CompletedActions.Count() == 0 ||
    gameState.CompletedActions.Last().Type == CompletedActionType.END_TURN) {
    usedTimeInTurn = TimeSpan.FromSeconds(0);
    rootNode = null;                 // hard reset every turn
}
```

No cross-turn persistence at all. Within a turn:

```csharp
void ProceedTree(Move move)
{
    if (rootNode is null) return;
    if (rootNode.childs is not null && move.Command != CommandEnum.END_TURN) {
        foreach (var child in rootNode.childs) {
            if (!child.stochastic && child.nodes?.Count == 1) {
                if (MoveComparer.AreIsomorphic(child.move, move)) { rootNode = child.nodes[0]; return; }
            }
        }
    }
    rootNode = null;
}
```

Reuse only through **deterministic, single-outcome** transitions; any draw or tavern refill drops the tree. A second guard `CheckIfSameGameStateAfterOneMove` compares hand/tavern/cooldown/draw as multisets.

Every `Node` stores a **full `SeededGameState`** plus a `List<Move>` — plausibly 1–3 KB each. At 10⁴–10⁵ nodes/turn this approaches the 256 MB competition limit; the per-turn reset is very likely deliberate memory safety. **No transposition table** (§6.11).

### 1.9 Delta: `Sakkirina` vs `SakkirinaSolo`

Otherwise identical (same tier list, rule base, policy, eval, bandit, chance machinery). Complete substantive delta:

| | `Sakkirina` (2nd, 70.6 %) | `SakkirinaSolo` (1st, 71.8 %) |
|---|---|---|
| Value per node | **two** values `wins0`, `wins1` | one value `wins` |
| Perspective | node's `CurrentPlayer.PlayerID`, negated on switch | fixed `myPlayerID` field |
| Rollout depth | own turn **+ full opponent turn** (`cycles = 2`) | own turn only |
| Selection score | `(wins0 + wins1) / 2` | `wins` |
| Per-move slice | `Math.Min(1.0, (9.8 - used)/4)` | `Math.Min(0.65, (9.8 - used)/4)` |
| RNG seed | `1234569u` | `12345679u` |
| Turn-switch | `if (switched) (v0,v1) = (child.wins0, 1 - v0)` | none |

`Sakkirina`'s opponent lookahead (`// 相手のシミュレーション`, "opponent simulation"):

```csharp
double e0 = Evaluate(gameState, playerId);
if (gameState.GameEndState is not null || cycles <= 1) return (e0, null);

do {   // play out the opponent's entire turn with the same greedy policy
    move = SimulationMove(possibleMoves, gameState, rng, 1);
    (gameState, possibleMoves) = gameState.ApplyMove(move);
} while (move.Command != CommandEnum.END_TURN);
PlayerEnum oppPlayerId = playerId == PlayerEnum.PLAYER1 ? PlayerEnum.PLAYER2 : PlayerEnum.PLAYER1;
double e1 = 1 - Evaluate(gameState, oppPlayerId);
return (e0, e1);
```

**Why Solo wins despite doing less:**

1. **`Evaluate` is not antisymmetric**, so `1 - Evaluate(state, opp)` is an invalid negamax negation. The tavern term `value -= 2*(int)tier` is applied identically from either perspective, so under negation it flips sign — the same tavern is simultaneously good and bad. The `-2·prestigeValue` offset has the same defect. `Sakkirina` averages a consistent value with an inconsistent one.
2. **The opponent is modelled by the bot's own crude greedy policy** — extra bias, not information.
3. **~2× cost per iteration**, and it also spends 1.0 s vs Solo's 0.65 s per move, so Solo covers **twice as many decisions per turn** (§5).

Head-to-head confirms: **`SakkirinaSolo` beats `Sakkirina` 57.0 % – 43.0 %.**

---

## 2. Patron selection strategy

There isn't one.

```csharp
public override PatronId SelectPatron(List<PatronId> availablePatrons, int round)
{
    return availablePatrons[rng.Next() % availablePatrons.Count];
}
```

**Identical in both bots. Uniformly random. `round` ignored.**

Remarkable, because `Evaluate` holds strong explicit patron opinions:

```csharp
if (patron == PatronId.DUKE_OF_CROWS) value -= 100;   // owning Crows favour is BAD (blocks the call)
else if (patron == PatronId.ORGNUM)   value += 15;
...
else if (patron == PatronId.ANSEI)    value -= 25;    // opponent owning Ansei is bad
```

and `SimulationPolicy` has a full patron-call priority table (§4.6). The bot knows which patrons it likes, then drafts blind.

**Draft order** (`ScriptsOfTribute.cs:75–110`):

| Pick | Chooser | Visible |
|---|---|---|
| 1 | PLAYER1 | full pool |
| 2 | PLAYER2 | pool − 1 |
| — | *(TREASURY inserted automatically)* | |
| 3 | PLAYER2 | pool − 2 |
| 4 | PLAYER1 | pool − 3 |

Always **4 patrons + Treasury** regardless of pool size. Sakkirina throws away all four picks.

Both bots that beat `SakkirinaSolo` head-to-head draft deliberately: `HQL_Plus_BOT` → `ORGNUM > DUKE_OF_CROWS > RAJHIN > RED_EAGLE` first pick, `ORGNUM > RAJHIN` later; `NAgent` → `PREFERRED_ORDER = [ORGNUM, SAINT_ALESSIA, ANSEI, RAJHIN, PELIN, DUKE_OF_CROWS]`. Both converge on **Orgnum first**. Sakkirina's tier list rates Orgnum almost entirely B/C (only *Snakeskin Freebooter* = S, *Serpentguard Rider* = A).

**This is free, deterministic, zero-risk EV — nothing else in this report is as cheap to exploit.**

---

## 3. Move generation, pruning, endgame detection, combo handling

### 3.1 Root move shuffling

Fisher–Yates on every `Play()` call to de-bias engine ordering. It also randomizes **all `BestChild` tie-breaks and all `RootRuleBasedMove` "first match wins" scans** — which is where it backfires badly (§3.3).

### 3.2 The rule base (main pruning mechanism)

`RootRuleBasedMove` runs **before** the search; a non-null return skips search entirely. It is also called in every `Node` constructor, where a hit collapses the node to a **single child** — total pruning.

Rules in order:

1. `if (moves.Count == 1) return moves[0];`
2. Resource-only cards / agents played immediately.
3. Obvious choices — `COMPLETE_TREASURY`: discard `BEWILDERMENT` first, else `GOLD`. `DESTROY_CARDS`: take the move whose choice set is *entirely* Bewilderment. `DISCARD_CARDS`: discard a lone `BEWILDERMENT`.
4. Guaranteed-profit combo: if `HLAALU` combo ≥ 2, buy `KWAMA_EGG_MINE`/`EBONY_MINE`. *(Dead in 2025 — Hlaalu not in the six-patron pool.)*
5. Endgame short-circuits:
   - `if (Prestige >= 80) return Move.EndTurn();`
   - `if (myPatronFavour >= 3)` → call the patron that (maybe) completes the sweep — **without verifying the call will actually flip the favour**; can waste a patron call.

The six card sets (quoted in full):

```csharp
resourceOnlyCards = {
  WRIT_OF_COIN, GOLD,                                          // Treasury
  GOODS_SHIPMENT, LUXURY_EXPORTS,                              // Hlaalu
  WAR_SONG, MIDNIGHT_RAID,                                     // Red Eagle
  PECK, SCRATCH, MURDER_OF_CROWS,                              // Crow
  FORTIFY, THE_PORTCULLIS, REINFORCEMENTS, LEGIONS_ARRIVAL,
  ARCHERS_VOLLEY, SIEGE_WEAPON_VOLLEY, THE_ARMORY,             // Pelin
  SWIPE, BEWILDERMENT, POUNCE_AND_PROFIT, GRAND_LARCENY,
  JARRING_LULLABY, SHADOWS_SLUMBER,                            // Rajhin
  SEA_ELF_RAID, MAORMER_BOARDING_PARTY, SUMMERSET_SACKING,
  GHOSTSCALE_SEA_SERPENT, SEA_SERPENT_COLOSSUS,
  SERPENTPROW_SCHOONER, PYANDONEAN_WAR_FLEET,                  // Orgnum
  MAINLAND_INQUIRIES                                           // Psijic
}

resourceOnlyAgents = {
  SHIELD_BEARER, BANGKORAI_SENTRIES, KNIGHTS_OF_SAINT_PELIN,
  BANNERET, KNIGHT_COMMANDER,                                  // Pelin
  BLACKFEATHER_KNIGHT,                                         // Crow
  ALESSIAN_REBEL, MORIHAUS_SACRED_BULL                         // Alessia
}

tavernExchangeCards  = { STORM_SHARK_WAVECALLER, SERPENTGUARD_RIDER, SLIGHT_OF_HAND, PRESCIENCE, PROPHESY }
drawingCards         = { POOL_OF_SHADOW, TOLL_OF_FLESH, TOLL_OF_SILVER, PILFER, PLUNDER, SQUAWKING_ORATORY, RALLY }
drawingAgents        = { BLACKFEATHER_BRIGAND, BLACKFEATHER_KNAVE }
selectingResourceCards = { WAY_OF_THE_SWORD, WARRIOR_WAVE }
combo3CoinContracts  = { KWAMA_EGG_MINE, EBONY_MINE }
zeroCostCards        = { GOLD, GOODS_SHIPMENT, WAR_SONG, PECK, ALESSIAN_REBEL,
                         FORTIFY, SWIPE, MAINLAND_INQUIRIES, SEA_ELF_RAID, BEWILDERMENT }
```

### 3.3 Combo handling — **this is the hole**

The only mention of combos in `SimulationPolicy`:

```csharp
} else if (move.Command == CommandEnum.PLAY_CARD) {
    var card = (move as SimpleCardMove).Card;
    if (resourceOnlyCards.Contains(card.CommonId)) logit += 50;
    else if (tavernExchangeCards.Contains(card.CommonId)) logit += 40;
    else if (selectingResourceCards.Contains(card.CommonId)) logit += 30;
    else if (drawingCards.Contains(card.CommonId)) logit += 20;
    // コンボ                    <-- "combo"
    /**/                        <-- empty. never written.
}
```

**The combo logic was never implemented.** The only other reference is the dead Hlaalu rule.

The consequence is worse than "no combo bonus". `resourceOnlyCards` contains `THE_ARMORY`, `SIEGE_WEAPON_VOLLEY`, `LEGIONS_ARRIVAL`, `ARCHERS_VOLLEY` (Pelin combo payoffs), the entire Orgnum suite, `MURDER_OF_CROWS`, and four Rajhin knockout cards. `RootRuleBasedMove` returns **the first match found while scanning `moves`** — and `moves` was Fisher–Yates shuffled at the top of `Play()`.

> **Sakkirina plays its combo-relevant cards in uniformly random order, and the search never sees those decisions at all.**

In ToT, combo N effects fire based on how many cards of that patron were already played this turn, so ordering is a first-order skill. Deck values include a weak composition term (`potentialComboNumber`, §4.3), but that measures *deck contents*, not *turn sequencing*, and is worth ~36 points against ~500 per S-tier card.

### 3.4 Depth limit

`if (child.visits < 1 || depth > 20)` — beyond 20 plies within a single turn the search degenerates to a rollout. Long combo/draw chains routinely exceed 20 moves.

---

## 4. Evaluation function — exact heuristics and weights

`Evaluate(SeededGameState, PlayerEnum) → double` in **(0,1)**. Identical in both bots.

### 4.1 Tier scale

```csharp
public enum TierEnum
{
    /* S = 1000, A = 400, B = 200, C = 90, D = 40, */   // an earlier, abandoned scale
    S = 50, A = 25, B = 10, C = 5, D = 1, UNKNOWN = 0,
}
static double SingleCardValue(Card card) { return (int)CardTierList.GetCardTier(card.Name) * 10; }
static double CardValue(Card card, SerializedPlayer player) { return SingleCardValue(card); } // player IGNORED
```

**Effective card values: S = 500, A = 250, B = 100, C = 50, D = 10, UNKNOWN = 0.** `Gold` and `Unknown` are both `UNKNOWN` → worth **0**.

### 4.2 Patron block

```csharp
foreach (var (patron, pId) in gameState.PatronStates.All) {
    if (patron == PatronId.TREASURY) continue;
    if (pId == playerID) {                       // I hold the favour
        myPatronFavour += 1;
        if (patron == PatronId.DUKE_OF_CROWS) value -= 100;   // "Crows becomes unusable when favoured"
        else if (patron == PatronId.ORGNUM)   value += 15;
        if (patron == PatronId.ANSEI) enemyPatronDistance += 1; else enemyPatronDistance += 2;
    } else if (pId == PlayerEnum.NO_PLAYER_SELECTED) {
        neutralPatronFavour += 1;                // computed, never used
        myPatronDistance += 1;
        enemyPatronDistance += 1;
    } else {                                     // opponent holds the favour
        enemyPatronFavour += 1;
        if (patron == PatronId.DUKE_OF_CROWS) value += 100;
        else if (patron == PatronId.ORGNUM)   value -= 15;
        else if (patron == PatronId.ANSEI)    value -= 25;
        if (patron == PatronId.ANSEI) myPatronDistance += 1; else myPatronDistance += 2;
    }
}
if (myPatronFavour >= 4) return 1;                        // patron-sweep win

value += (myPatronFavour - enemyPatronFavour) * 20;
if      (enemyPatronDistance == 1) value -= 3000;         // opponent one call from a sweep
else if (enemyPatronDistance == 2) value -=  300;
else if (enemyPatronDistance == 3) value -=   30;
if      (myPatronDistance == 1)    value +=   50;
else if (myPatronDistance == 2)    value +=    5;
```

`*PatronDistance` = number of patron calls that side needs to own all four (Ansei counts 1 instead of 2 because its favour flips in one call).

**Note the 60:1 asymmetry: `-3000` for the opponent one call away vs `+50` for itself one call away.** Sakkirina is patron-*defensive*; its search almost never steers toward its own patron win. That win happens only reactively via the `myPatronFavour >= 3` rule.

### 4.3 Deck value

```csharp
static double DeckValue(List<UniqueCard> allCards, bool enemy, double progress)   // 'enemy' is IGNORED
{
    double value = 0;
    Dictionary<PatronId,int> potentialComboNumber = new();
    int writOfCoinCount = 0;
    double cardCountCoef = Math.Sqrt(Math.Max(1.0, allCards.Count / 14.0));

    foreach (var card in allCards) {
        value += SingleCardValue(card) / cardCountCoef;
        if (card.Deck == PatronId.TREASURY) {
            if (card.CommonId == CardId.WRIT_OF_COIN) writOfCoinCount += 1;
        } else {
            if (card.CommonId == CardId.BEWILDERMENT) value -= 30 / cardCountCoef;
            else if (potentialComboNumber.ContainsKey(card.Deck)) potentialComboNumber[card.Deck] += 1;
            else potentialComboNumber[card.Deck] = 1;
        }
    }

    value += (40 - 20 * progress) * writOfCoinCount / cardCountCoef;

    foreach (KeyValuePair<PatronId,int> entry in potentialComboNumber) {
        value += Math.Pow(entry.Value, 1.5);                                              // "max combo count"
        value += Math.Pow(entry.Value / (double)allCards.Count, 3) * Math.Min(7, entry.Value) * 20;
    }
    return value;
}
```

Applied as `value += DeckValue(mine) - DeckValue(theirs)`.

Sizing check, 14-card starting deck (`cardCountCoef = 1`):

| Term | Magnitude |
|---|---|
| One S-tier card | **+500** |
| One A-tier card | +250 |
| One B-tier card | +100 |
| One Bewilderment | 10 − 30 = **−20** |
| One Writ of Coin (early) | 10 + 40 = +50 |
| Deck-focus, 7 cards of one patron | `7^1.5 + (7/14)³·7·20` = 18.5 + 17.5 = **+36** |

> **The two synergy terms total ~36 points against ~500 for one S-tier card. Sakkirina's evaluation is, to within a few percent, "average card tier × √(deck size)". It does not meaningfully value deck focus.**

The `√` normalization does correctly discourage dilution: adding a card to a 14-card deck of total value Σ helps only if its value exceeds ≈ `0.035·Σ`. With Σ ≈ 1000 that's a ~35 threshold → it buys anything C-tier or better, refuses D.

**Bewilderment is under-priced at −20 net.** Meanwhile the *choice* rules give Bewilderment removal a `+10000` override — fanatical about removing it, nearly indifferent to acquiring it.

### 4.4 Prestige and win conditions

```csharp
if (currentPlayer.Prestige >= 80) return 1;
if (enemyPlayer.Prestige >= 40 && currentPlayer.Prestige < enemyPlayer.Prestige) return 0;

double progress = Math.Min(Math.Max(currentPlayer.Prestige, enemyPlayer.Prestige), 40) / 40.0;
double prestigeValue = 10 + progress * 55 + (currentPlayer.Prestige >= 40 ? 10 : 0);

value -= prestigeValue * 2;
value += (currentPlayer.Prestige - enemyPlayer.Prestige) * prestigeValue;
if      (enemyPlayer.Prestige == 79) value -= 1000;
else if (enemyPlayer.Prestige == 78) value -=  300;
else if (enemyPlayer.Prestige == 77) value -=  100;
else if (enemyPlayer.Prestige == 76) value -=   30;
```

Both hard returns are **correct terminal detection** — verified against `BoardManager.CheckAndGetWinner`.

The two lines combine to `value += prestigeValue * (myPrestige − oppPrestige − 2)`, i.e. **it treats "+2 prestige" as break-even** — a deliberate tempo/second-player correction.

`prestigeValue` ranges **10 → 75**, and **`progress` saturates at 40**. Two consequences:

- **Early game, 1 prestige = 10 points while one S-tier card = 500.** One S-tier card ≈ 50 prestige to this bot. It is a hard deck-building greed bot in the first half.
- **The urgency weight maxes out exactly when the 40-threshold race begins** and never rises for the 40→80 stretch. Panic terms exist for enemy prestige 76–79 (the 80 race) but **nothing at all for 33–40 (the far more decisive 40 race)**.

**`Power` and `Coins` never appear in `Evaluate`.** Defensible: `Evaluate` only runs on states where `END_TURN` was already applied (`EndTurn()` folds Power into Prestige and zeroes Coins) — but it means the eval has **zero resolution on intra-turn resource states**.

### 4.5 Agents and tavern

```csharp
static double AgentValue(SerializedAgent agent)
{
    var tier = CardTierList.GetCardTier(agent.RepresentingCard.Name);
    return 10 * (int)tier + agent.CurrentHp * 3;
}
...
foreach (SerializedAgent agent in currentPlayer.Agents) value += AgentValue(agent);
foreach (SerializedAgent agent in enemyPlayer.Agents)  value -= AgentValue(agent) * 2 + 40;  // "opponent uses agents first"
...
foreach (var card in gameState.TavernAvailableCards) {
    var tier = CardTierList.GetCardTier(card.Name);
    value -= 2 * (int)tier;
}
return 1.0 / (1 + Math.Exp(-value / 900.0));
```

- Its own S-tier 4 HP agent = 500 + 12 = **+512**.
- The **same agent on the opponent's board** = −(2·512 + 40) = **−1064** — more than two S-tier cards.
- A B-tier 3 HP enemy agent = −(2·109 + 40) = **−258**. The flat `+40` means *every* enemy agent, however trivial, costs a quarter of an S-tier card.
- Tavern: all five slots S-tier = **−500**. It strongly wants a weak tavern → it makes denial buys.

The tavern term is **perspective-independent**, which is what breaks `Sakkirina`'s `1 - Evaluate(opp)` negation (§1.9).

Final squash `sigmoid(value / 900)`: 900 points = one e-fold; the `-3000` patron panic → sigmoid(−3.33) ≈ 0.034.

### 4.6 `SimulationPolicy` — rollout / prior logits

**`BoardState.NORMAL`:**

```csharp
END_TURN:        logit -= 10000;                      // always last
PLAY_CARD:       resourceOnlyCards      → +50
                 tavernExchangeCards    → +40
                 selectingResourceCards → +30
                 drawingCards           → +20
CALL_PATRON:     logit -= 100;
                 TREASURY:       logit += 5 - progress * 3;  (+1 if Coins==2; −5 if no zero-cost played; +0.5 if Bewilderment played)
                 ORGNUM:         logit += 3 + (patronStatus == playerID ? 1 : 0)
                 DUKE_OF_CROWS:  logit += (Coins - 5) + progress * 2.5   // "bad early"
                 ANSEI:          logit += 2 - progress
                 PELIN:          logit += 1
                 else:           logit += 0.5
                 (+0.5 if patron not ours; +0.5 more if myPatronFavour >= 2 and not ours)
BUY_CARD:        logit -= 20;  logit += CardValue(card) * 0.05;  logit += myDeckCount[card.Deck] * 1
ACTIVATE_AGENT:  logit -= 3;  resourceOnlyAgents → +0.5;  drawingAgents → +0.2
ATTACK:          logit -= 2;  logit += CardValue(card) * 0.2          // S agent → +100
```

**Choice states:**

```csharp
COMPLETE_TREASURY: BEWILDERMENT → +10000; GOLD → +10; other zeroCost → +3; logit -= CardValue(card) * 0.2
DESTROY_CARDS:     same scoring, summed over all cards in the choice set
DISCARD_CARDS:     if (choices.Count != 1) logit -= 1000;  else same scoring
KNOCKOUT_AGENTS:   enemy agent → logit += AgentValue(agent) * 0.2;  own agent → logit -= 3
ACQUIRE_CARDS:     logit += CardValue(card)              // full weight
REFRESH_CARDS:     logit += CardValue(card) * 0.3
COMPLETE_PELLIN:   logit -= CardValue(card) * 0.3
COMPLETE_PSIJIC:   logit -= CardValue(card) * 0.3
COMPLETE_HLAALU:   logit += card.Cost * 1.5 - CardValue(card) * 0.1
```

**Implied rollout ordering:** play cards (0…+50) → attack (−2) → activate agent (−3) → buy (−20) → call patron (−100) → end turn (−10000).

**Temperature.** `Simulate` calls `SimulationMove(..., temperature = 1)` and softmaxes `exp((logit − best)/1)`. With logit gaps of 10–50, a gap of 10 gives a ratio of e⁻¹⁰ ≈ 4.5×10⁻⁵. **Temperature 1 over these logits is effectively argmax.** Rollouts are near-deterministic: low variance, high bias.

Downstream consequence: its evaluation of a position is really *"what does my greedy policy achieve from here"*, so it systematically **underestimates every line requiring non-greedy play** — banking coins for a bigger buy next turn, correct combo ordering, deliberate tempo sacrifices.

---

## 5. Time budget usage

### 5.1 The engine's clock

The 10 s (`config.yaml: timeout: 10`) is a **cumulative budget across every `Play()` call in one turn**, reset at end of turn. Overrun = **instant loss**, not a forfeited move. `TurnLimit = 500`.

### 5.2 Sakkirina's allocation

```csharp
TimeSpan TurnTimeout = TimeSpan.FromSeconds(9.8);         // self-imposed, 200 ms margin
...
if (usedTimeInTurn >= TurnTimeout) {
    move = SimulationMove(possibleMoves, sgs, rng, 0);    // temperature 0 = pure greedy policy, no search
    ProceedTree(move); return move;
}

// Sakkirina:     Math.Min(1.00, (TurnTimeout - usedTimeInTurn).TotalSeconds / 4)
// SakkirinaSolo: Math.Min(0.65, (TurnTimeout - usedTimeInTurn).TotalSeconds / 4)
```

A **geometric decay** schedule: flat cap until the remaining budget drops below 4× the cap, then 0.75× per decision, asymptotically approaching 9.8 s.

- Both budget **9.8 s of the 10 s**, leaving a **200 ms margin**.
- `Sakkirina` gets **~6 full-strength decisions per turn**; `SakkirinaSolo` gets **~12** — twice as many.
- Past 9.8 s, all remaining decisions use the **greedy policy with zero search** (`temperature = 0`).
- Most moves never consume budget at all — `RootRuleBasedMove` or `moves.Count == 1` short-circuits first.

Typical turn with 3–6 hard decisions: **3–6 s**. Complex turns: saturate at **~9.8 s**.

### 5.3 Unaccounted overhead (a real timeout risk)

`usedTimeInTurn` accumulates **only `s.Elapsed` from inside the search loop**. Not counted per `Play()` call: the Fisher–Yates shuffle; `ToSeededGameState`; `RootRuleBasedMove`; `CheckIfSameGameStateAfterOneMove`; `new Node(...)` (invokes `SimulationPolicy` per move); the final `AreIsomorphic` scan; **overshoot of the last `TreeSearch` iteration**; and **GC pauses** on a tree holding 10⁴–10⁵ full `SeededGameState`s. On a long turn with 40–60 `Play()` calls this plausibly runs 100–300 ms — the same order as the entire margin. **Forcing maximally long, choice-heavy turns is a legitimate pressure vector.**

### 5.4 The dominant CPU cost

```csharp
public static TierEnum GetCardTier(string cardName)
{
    try { return Array.Find(CardTierArray, x => x.Name == cardName).Tier; }
    catch { return TierEnum.UNKNOWN; }
}
```

A **linear scan over 124 entries with string equality**, plus a fresh closure allocation per call, on the hottest path. ~50–90 lookups × up to 124 string comparisons ≈ **5000–10000 string comparisons per leaf evaluation**. Unknown names throw a caught `NullReferenceException` each lookup. A `Dictionary<CardId, TierEnum>` or flat array is worth an estimated **3–10× throughput**.

---

## 6. Weaknesses and exploitable patterns

Ordered by estimated exploitable value.

1. **Random patron drafting** — free EV, zero risk (§2).
2. **Eval is card-quality greed with no synergy model** (§4.3) — commit to 1–2 patrons and out-tempo it.
3. **Combo sequencing randomized and unsearched** (§3.3) — draft Pelin/Orgnum/Alessia where sequencing matters most.
4. **Five-bucket card resolution → frequent ties → arbitrary choices** (tie-break = first in shuffled list).
5. **No graduated model of the 40-prestige race** (§4.4) — burst decks that spike 25 → 45+ in one or two turns are its cleanest strategic counter.
6. **Single determinization per turn** (§1.6) — hidden-information play (holding combos back, dual threats) is invisible to it.
7. **Enemy agents massively over-feared** (§4.5) — cheap disposable agents bait its Power away from prestige.
8. **Patron threats −3000 vs own patron win +50** (§4.2) — sweep threats drag it off plan; it will never race us to a patron win.
9. **Near-deterministic greedy rollouts** (§4.6) — undervalues every non-greedy line.
10. **`child.prob` neutered** (§1.3) — plain UCT; a real PUCT prior is available to us.
11. **No transposition table** — order-independent card plays searched as distinct subtrees.
12. **`GetCardTier` linear string scan** (§5.4) — 3–10× throughput on the table.
13. **Thin timeout margin with unaccounted overhead** (§5.3).
14. **Fully deterministic, fixed seed, never reseeded between games** — offline exploit search is viable.
15. **Dead/buggy code**: discarded `OrderBy` result (prior ordering never happens); unused `neutralPatronFavour`, `anyInvalidMoves`; ignored `player`/`enemy` params; dead Hlaalu/Psijic/Red Eagle branches (live and untested if the 2026 pool changes); favour-sweep rule can waste a call.
16. **Sakkirina's two-value negamax is inconsistent** (§1.9) — do not copy.

### 6.17 What the head-to-head matrix says

| | vs `Sakkirina` | vs `SakkirinaSolo` | field WR |
|---|---|---|---|
| `HQL_Plus_BOT` | **0.535** | **0.526** | 69.2 % |
| `NAgent` | 0.405 | **0.563** | 50.4 % |
| `BestMCTS3` | 0.418 | 0.385 | 66.1 % |

**`HQL_Plus_BOT` beats both Sakkirina variants head-to-head and still placed 3rd** — it lost the tournament on the *rest* of the field (0.486 vs `EBot_C`, 0.502 vs `NAgent`). The most actionable datum in the archive: **a beam search over complete turn lines with a 7-way determinization vote and deliberate patron drafting already beats the winner.**

`NAgent` (pure LSTM+attention policy net, single forward pass, no search) beats `SakkirinaSolo` 56.3 % but loses to `Sakkirina` 40.5 %.

**Read across all three:** the winner's margin comes from the *field*, not from being unbeatable. Two very different architectures already beat it head-to-head.

---

## 7. Concrete plan for a substantially different, stronger bot

### 7.0 Design thesis

Do not build a better MCTS. Sakkirina's ceiling is set by its **leaf evaluation** and its **N = 1 determinization**, not by its search.

**Proposal: a turn-level stochastic optimizer over complete turn plans, with a determinization ensemble and a learned evaluation.**

### 7.1 Priority 1 — the free wins

1. **Draft patrons deliberately** (patron-pair value table conditioned on draft position; field consensus Orgnum first — verify empirically).
2. **`CardId`-indexed array lookups** instead of string scans — 3–10× throughput alone.
3. **Incremental evaluation** — deltas across a move, not re-summing both decks.
4. **Transposition table** (Zobrist hash over hand/played/coins/power/prestige/agents/tavern/combo counters).
5. **Correct combo sequencing**, even as a pure rule.

### 7.2 Priority 2 — the architectural difference

6. **Determinization ensemble, K ≥ 8**, root-level vote or expected-value aggregate, with common random numbers across candidate plans.
7. **Turn-plan optimization by simulated annealing / parallel tempering** over complete move sequences, objective = mean end-of-turn eval across K determinizations.
8. **Opponent belief model** — particle filter over their deck composition conditioned on observed buys/plays.

### 7.3 Priority 3 — the evaluation

9. **Learn the evaluation** (logistic regression or small MLP on self-play outcomes).
10. **Model the 40-prestige threshold race explicitly** (turns-to-threshold feature).
11. **Value deck focus properly** (probability of hitting combo 2/3/4 given composition and hand size).
12. **Fix the agent-value asymmetry**.

### 7.4 Priority 4 — direct anti-Sakkirina tactics

13. Race the 40-prestige threshold. 14. Bait with cheap agents. 15. Threaten patron sweeps. 16. Feed it Bewilderment via Rajhin. 17. Keep the tavern stocked with cards its tier list rates S/A (bribes denial buys). 18. Probe for timeouts with long choice-heavy turns (verify first; bonus, not plan).

### 7.5 Engineering notes

- Do not copy the two-value negamax. If we want opponent lookahead, make the eval genuinely antisymmetric first.
- **Do copy the chance-node design**: stochasticity detection, progressive widening at `visits^0.3`, shared sibling seeds.
- Keep budget below 9.8 s and account for **all** per-call overhead.
- Watch 256 MB: compact state representation (or move-path replay) would allow cross-turn trees — something no 2025 bot does.
- Confirm the 2026 patron pool before finalizing draft logic (archived pool: Pelin, Crows, Ansei, Rajhin, Orgnum, Alessia; current Core exposes nine + `--patrons` flag).
