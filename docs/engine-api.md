# Scripts of Tribute — Engine API Reference

Practical reference for writing a competitive bot against `ScriptsOfTribute-Core`. Source paths below
are relative to `/Users/manan/WORK/COG/scripts-tribute/ScriptsOfTribute-Core/`.

Contents:
1. [The `AI` base class contract](#1-the-ai-base-class-contract)
2. [`GameState` vs `SeededGameState`](#2-gamestate-vs-seededgamestate)
3. [Moves and the choice system](#3-moves-and-the-choice-system)
4. [Game rules data (patrons, win conditions, combos)](#4-game-rules-data)
5. [Timeouts and how GameRunner enforces them](#5-timeouts-and-how-gamerunner-enforces-them)
6. [Performance notes for search bots](#6-performance-notes-for-search-bots)
7. [Adding a bot and running it](#7-adding-a-bot-and-running-it)

---

## 1. The `AI` base class contract

File: `Engine/src/AI/AI.cs`

```csharp
public abstract class AI
{
    public PlayerEnum Id { get; set; } = PlayerEnum.NO_PLAYER_SELECTED;

    public virtual void PregamePrepare() {}
    public abstract PatronId SelectPatron(List<PatronId> availablePatrons, int round);
    public abstract Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime);
    public abstract void GameEnd(EndGameState state, FullGameState? finalBoardState);

    protected void Log(string message);
}
```

Your bot subclasses `AI` and must implement `SelectPatron`, `Play`, `GameEnd`. `PregamePrepare` is
optional. `Id` is set for you by the engine (`PLAYER1`/`PLAYER2`) before any callback fires — you can
read it but shouldn't set it yourself.

### `PregamePrepare()`
Called once before the match (e.g. to warm up caches, load a model, connect a gRPC bridge). Budget:
**5 seconds** hard limit, enforced in `ScriptsOfTribute.cs::PrepareBots` via
`Task.Wait(TimeSpan.FromSeconds(5))`. Timing out ends the game immediately with
`GameEndReason.PREPARE_TIME_EXCEEDED`, and you lose — the *other* player is declared winner. No moves
are ever played.

### `SelectPatron(List<PatronId> availablePatrons, int round)`
Called **exactly twice per game** (patron drafting is snake-order across two rounds: P1, P2, P2, P1 —
see `ScriptsOfTribute.cs::PatronSelection`). `round` is `1` or `2` telling you which draft round this
is; it does not tell you which "pick number" you are (you can infer pick order from `availablePatrons`
shrinking). Must return one of the values in `availablePatrons`. The game always force-adds
`PatronId.TREASURY` as the third patron (not draftable, not passed to `SelectPatron`) — three patrons
plus Treasury make four; Treasury's favor can never be won by either side (see §4).

Timeout budget for each `SelectPatron` call is the same `Timeout` value passed to the
`ScriptsOfTribute` constructor (default 30s via GameRunner `--timeout`). Returning an invalid patron
(not in the offered list, or not a defined enum value) ends the game immediately with
`GameEndReason.PATRON_SELECTION_FAILURE` — you lose. Timing out ends the game with
`GameEndReason.PATRON_SELECTION_TIMEOUT` — you lose.

### `Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)`
Called repeatedly in a loop for **every single move you make within your turn** — not once per turn.
A typical turn calls `Play` many times: play a card, resolve any triggered choice, play another card,
attack, buy, call a patron, and finally `END_TURN`. The loop only breaks when you return a `Move` with
`Command == CommandEnum.END_TURN` (`ScriptsOfTributeGame.cs::Play`, inner `do...while`).

You must return one of the `Move` objects (by reference-equal `Command`/payload match; see §3) from
`possibleMoves` — anything else is `GameEndReason.INCORRECT_MOVE` and an instant loss. The error
message you'd get server-side/log-side includes the illegal move plus the full list of what was legal.

`remainingTime` is **time left in the current turn's shared budget**, not time left for this single
`Play` call. See §5 — the clock is the turn's `Timeout` (`ScriptsOfTribute.Timeout`, default 30s from
GameRunner `-to`), and it only resets to full when you actually end your turn. Every `Play` call inside
the turn (including ones answering a pending choice) draws down the same pool of time; the engine
subtracts your call's wall-clock duration from what's left and passes the remainder into the next call.
If you don't return in time — even on move #6 of a turn after having been fast on moves #1–5 — you
forfeit (`GameEndReason.TURN_TIMEOUT`).

`gameState` is a fresh `GameState` object built from the live board every single call — see §2 for what
it exposes. `possibleMoves` is the exhaustive legal move list for the *current* decision point, which
may be a `MAKE_CHOICE` list instead of the normal action list if a card/patron effect put you into a
pending choice (see §3).

### `GameEnd(EndGameState state, FullGameState? finalBoardState)`
Called once, on both players, when the game concludes for any reason (normal win, timeout, exception,
disqualification, turn-limit). `finalBoardState` is `null` only if the game ended before a board even
existed (pregame-prepare timeout or patron-selection failure/timeout) — otherwise it's the full,
un-redacted final board (both hands visible; safe to inspect since the game is over). Use this to reset
any turn-scoped state in your bot (the `AI` instance is *not* automatically recreated between games —
see §6 for what that implies with `GameRunner -n`).

### `Log(string message)`
Appends to `LogMessages`, drained by the engine after every `Play`/patron-selection call and forwarded
to whichever `TextWriter` (`P1LogTarget`/`P2LogTarget`) or log file GameRunner configured. Not
performance-relevant, but note it's cleared after every consumption — don't rely on it persisting.

### `ExternalAIAdapter` / gRPC
`Engine/src/AI/ExternalAIAdapter.cs` is a **deprecated** stdin/stdout bridge for non-C# bots (marked
"OBSOLETE" in the README). The supported way to write a bot in another language is gRPC
(`gRPC/gRPCBot.cs`, driven through the `scripts-of-tribute` PyPI package for Python) — invoke it from
GameRunner with the `grpc:<BotName>` prefix (§7).

---

## 2. `GameState` vs `SeededGameState`

There are three board representations in play; understanding which one hides what is the single most
important thing for writing a legal, non-cheating bot.

| Class | File | Where you get it | Hidden info respected? |
|---|---|---|---|
| `FullGameState` | `Engine/src/Serializers/FullGameState.cs` | Internal engine state, and what you get back from `GameEnd`'s `finalBoardState` | No — everything visible (game is over or it's the engine's private truth) |
| `GameState` | `Engine/src/Serializers/GameState.cs` | Passed into `Play()` every call | **Yes** — this is the "fair" view |
| `SeededGameState` | `Engine/src/Serializers/SeededGameState.cs` | You build it yourself via `GameState.ToSeededGameState(seed)` | Partially — hidden info is *filled in with a plausible guess*, not real data |

### `GameState` — what's observable
`GameState` wraps a `FullGameState` but re-exposes the two players through different types:

- `CurrentPlayer` is a `FairSerializedPlayer` — **your own hand is fully visible** (`Hand`), your
  `CooldownPile`/`Played`/`Agents`/`Power`/`Coins`/`Prestige`/`PatronCalls` are visible, and your
  `DrawPile` is visible but returned **sorted by `CommonId`** (`_player.DrawPile.OrderBy(c =>
  c.CommonId)`) — you know the *composition* of your draw pile but not its *shuffled order*, except for
  `KnownUpcomingDraws` (cards a "look at the top N of your deck" effect revealed — those keep their true
  order).
- `EnemyPlayer` is a `FairSerializedEnemyPlayer` — you see `Agents`, `Power`, `Coins`, `Prestige`,
  `Played`, `CooldownPile`, but **not** a separate `Hand`/`DrawPile`. Instead you get `HandAndDraw`: the
  opponent's hand and draw pile *concatenated and sorted by `CommonId`* — you know exactly which cards
  the opponent currently holds across hand+deck combined, but not the hand/deck split and not the deck
  order. This is deliberate hidden-information design, not a bug: real Tales of Tribute lets you count
  opponent cards this way.
- `TavernCards` (the face-down tavern deck, as opposed to the 5 `TavernAvailableCards` face-up) is
  exposed **sorted by `CommonId`**, same idea — composition known, order hidden.
- `CompletedActions`, `PatronStates`, `ComboStates`, `UpcomingEffects`, `PendingChoice`,
  `TavernAvailableCards`, `BoardState`, `GameEndState` are all visible as-is.
- `StateId` is a fresh GUID per `GameState` construction — don't treat it as stable/comparable across
  calls.

### `SeededGameState` — determinization for simulation
`GameState.ToSeededGameState(ulong seed)` produces a `SeededGameState` where all the previously-hidden
randomness is **resolved into one concrete, seed-determined possible world**:
`SeededGameState`'s constructor (`SeededGameState.cs`) uses a `SeededRandom(seed)` to:
- shuffle your own cooldown pile and interleave it with your draw order (beyond the known-upcoming
  prefix),
- **redistribute the opponent's hand and draw pile**: it pools `player.Hand.Concat(player.DrawPile)`,
  shuffles with the seed, and re-splits into a hand of the original size and a draw pile — i.e. it
  invents one specific plausible opponent hand/deck consistent with what you can observe (composition),
  not the true one,
- shuffle the face-down tavern deck.

The result: `SeededGameState.CurrentPlayer` / `.EnemyPlayer` are now plain `SerializedPlayer` objects
with concrete `Hand`/`DrawPile` you can inspect directly — convenient for simulation, but the
opponent's exact hand is a **guess**, freshly re-guessed every time you call `ToSeededGameState` (or
`ApplyMove` with a new seed). Two calls with the same seed on the same `GameState` produce the same
guess; different seeds produce different guesses. This is exactly the "determinization" trick classic
in imperfect-information game search (ISMCTS-style): sample a concrete world, search it as if it were
perfect information, repeat with different seeds to average over uncertainty.

`InitialSeed` / `CurrentSeed` on `SeededGameState`/`FullGameState` reflect the *engine's own* internal
RNG stream (used for real shuffles/draws), which is separate from whatever seed *you* pass into
`ToSeededGameState`/`ApplyMove` for your own simulations.

### `ApplyMove` — signature, cost, semantics
Both `GameState` and `SeededGameState` expose `ApplyMove`, always returning the *new* state plus the
*new* legal move list (you never have to call `GetListOfPossibleMoves` separately):

```csharp
// GameState (fair view) -> always produces a SeededGameState
(SeededGameState, List<Move>) GameState.ApplyMove(Move move, ulong seed);

// SeededGameState -> SeededGameState, two overloads
(SeededGameState, List<Move>) SeededGameState.ApplyMove(Move move);            // seed = 0 internally... see below
(SeededGameState, List<Move>) SeededGameState.ApplyMove(Move move, ulong seed);
```

Important nuance on the seedless `SeededGameState.ApplyMove(move)` overload: it calls
`new SeededGameState(newBoard)` — the **single-argument** constructor, which does **not** scramble
anything; it just wraps the already-resolved `FullGameState` from the underlying `ApplyMove`. That
underlying `FullGameState.ApplyMove` (see below) resolves the move through a *real* `ScriptsOfTributeApi`
built from the current board via `BoardManager.FromSerializedBoard`, so any actual randomness the move
triggers (e.g. drawing a card, tavern refill) is driven by the **engine's own carried-forward RNG
stream** (`FullGameState.InitialSeed`/`CurrentSeed`), not a value you control. Practically:
- Use `ApplyMove(move, seed)` when you want a **fresh, seed-controlled reshuffle of hidden information**
  after the move (e.g. redeterminizing the opponent's hand for the next simulation step).
- Use the seedless `ApplyMove(move)` when you just want to advance state deterministically along the
  engine's own RNG stream without re-randomizing hidden info — e.g. replaying a rollout you're already
  committed to.
- `MaxPrestigeBot` (`Bots/src/MaxPrestigeBot.cs`) shows a 1-ply and 2-ply lookahead using a fixed
  constant seed (`Seed = 123`) for every `ApplyMove` call, checking `newState.GameEndState?.Winner ==
  Id` after each step to detect immediate/near-term wins.
- `MCTSBot` and `BeamSearchBot` (below) call `gameState.ToSeededGameState(botSeed)` once per `Play()`
  entry with a fixed bot-owned seed, then chain `.ApplyMove(move)` (no seed) through the rest of the
  simulated turn/rollout.

What actually happens on `ApplyMove` (`FullGameState.cs::ApplyMove`): it rebuilds a full
`ScriptsOfTributeApi`/`BoardManager` from the serialized board (`FromSerializedBoard`), replays the one
move through it (`PlayCard`/`ActivateAgent`/`AttackAgent`/`BuyCard`/`PatronActivation`/`MakeChoice`/
`EndTurn`), and re-serializes. **This means every `ApplyMove` call reconstructs the entire board object
graph from scratch** — see §6 for what that costs.

`ApplyMove` does not return an `EndGameState?` directly — check `newState.GameEndState` on the returned
state after the call (it's `null` while the game continues). It also does not validate legality for you
the way the real turn loop does (no `IsMoveLegal` check) — feeding it an illegal move throws inside the
API layer (uncaught `EngineException` propagates out of `ApplyMove`, unlike during a real turn where
`ScriptsOfTributeApi.Try` catches it and turns it into an `EndGameState`). **Only ever call `ApplyMove`
with moves drawn from a `possibleMoves` list you already have** — don't invent moves.

---

## 3. Moves and the choice/pending-choice system

File: `Engine/src/Board/Move.cs`. `Move` is abstract-ish (constructors are `protected`/`internal`) —
always build moves via the static factories:

| `CommandEnum` | Factory | Payload | Meaning |
|---|---|---|---|
| `PLAY_CARD` | `Move.PlayCard(UniqueCard card)` | card (must be in your `Hand`) | Play a card from hand |
| `ACTIVATE_AGENT` | `Move.ActivateAgent(UniqueCard card)` | card (must be your un-activated `Agent`) | Trigger an agent's tap ability |
| `ATTACK` | `Move.Attack(UniqueCard card)` | card (enemy agent) | Attack an enemy agent with your `Power` |
| `BUY_CARD` | `Move.BuyCard(UniqueCard card)` | card (in `TavernAvailableCards`) | Buy from the 5 face-up tavern slots |
| `CALL_PATRON` | `Move.CallPatron(PatronId id)` | patron id | Spend a patron call to activate a patron |
| `MAKE_CHOICE` | `Move.MakeChoice(...)` | `List<UniqueCard>` or `List<UniqueEffect>` (or single-item overloads) | Resolve a pending choice |
| `END_TURN` | `Move.EndTurn()` | — | End your turn |

`Move.Equals`/`GetHashCode` compare by `Command` (+ payload for the subclasses) — `possibleMoves` is a
real `List<Move>` you should treat as your only source of legal actions each call; don't construct your
own `Move` objects to hand back except by copying one out of `possibleMoves` (card object identity via
`UniqueId` matters — see below).

### `UniqueCard` identity
Every card instance in the game (even multiple copies of the "same" card) gets a distinct `UniqueId`
(`Board/Cards/UniqueId.cs`) at creation. `Card` (the immutable template, `Cost`/`Type`/`Effects`/etc.)
is shared; `UniqueCard` wraps it with that per-instance id. `UniqueCard.Equals`/`==` compare by
`UniqueId`, not by `CommonId`/name — **the exact `UniqueCard` object you got from `possibleMoves` (or
from `gameState.CurrentPlayer.Hand`, etc.) is what you must pass back into `Move.PlayCard(...)`** — a
different `UniqueCard` instance with the same `CommonId`/name will not `Equals()`-match and your move
will be rejected as illegal.

### `GetListOfPossibleMoves()` — how the legal set is built
`ScriptsOfTributeApi.cs::GetListOfPossibleMoves`. If a choice is pending (`BoardState !=
NORMAL`), the **only** legal moves are `MAKE_CHOICE` moves — every combination of
`MinChoiceAmount..MaxChoiceAmount` items out of `Choice.PossibleCards`/`PossibleEffects`
(`Extensions.GetCombinations<T>`, `Engine/src/utils/Extensions.cs`). This can combinatorially explode:
choosing "any 3 of 6 cards" yields `C(6,3) = 20` distinct `MAKE_CHOICE` moves, and combinations are
generated for **every** k from `MinChoiceAmount` to `MaxChoiceAmount` inclusive, all concatenated into
one list.

Otherwise (`BoardState == NORMAL`), the legal set is the union of:
- `PLAY_CARD` for every card in `Hand`,
- `ACTIVATE_AGENT` for every un-activated agent you control,
- `ATTACK` for enemy agents *if* you have `Power > 0` — restricted to taunt agents only if any exist
  (`enemyPlayer.Agents.FindAll(agent => agent.RepresentingCard.Taunt)`; if none, all enemy agents are
  attackable),
- `BUY_CARD` for every tavern-available card affordable with current `Coins`,
- `CALL_PATRON` for every patron whose `CanPatronBeActivated` returns true, if you still have
  `PatronCalls > 0` (starts at 1 per turn, some cards/patrons grant more),
- always, `END_TURN`.

`IsMoveLegal(Move)` just recomputes this whole list and does `.Contains(move)` — it's not cheap, don't
call it defensively in a hot loop; trust the `possibleMoves` list you were handed.

### `BoardState` and the choice loop
`Board/CardAction/CardActionManager.cs`:

```csharp
public enum BoardState { NORMAL, CHOICE_PENDING, START_OF_TURN_CHOICE_PENDING, PATRON_CHOICE_PENDING }
```

- `NORMAL` — ordinary decision point, full move menu as above.
- `CHOICE_PENDING` — a card/combo effect (e.g. "destroy a card", "discard N cards", "acquire a card")
  produced a `Choice` that hasn't resolved yet; only `MAKE_CHOICE` moves are legal until it's answered.
  This can chain — resolving one choice can immediately produce another (`ConsumePendingChainToChoice`
  loops until either everything resolves or another `Choice` appears).
- `START_OF_TURN_CHOICE_PENDING` — specifically the "you must destroy a card from a `StartOfNextTurn`
  effect" pattern that fires at the very top of your turn, before your first ordinary `Play` call for
  that turn (`ScriptsOfTributeGame.cs::HandleStartOfTurnChoices`). Currently the engine asserts this is
  always a `CARD`-type choice.
- `PATRON_CHOICE_PENDING` — you called a patron (e.g. Pelin) whose activation itself produces a choice
  (e.g. "return an agent from cooldown to top of deck — which one").

`ChoiceContext` (`Board/CardAction/ChoiceContext.cs`) tells you *why* you're being asked: it carries
`PatronSource` (patron activations), or `CardSource` + `Combo` (which combo tier of which card
triggered it) + derived `Effect`. `SerializedChoice` (`Engine/src/Serializers/SerializedChoice.cs`,
reachable as `gameState.PendingChoice`) additionally exposes `MinChoices`/`MaxChoices`,
`ChoiceFollowUp` (an enum telling you the *kind* of resolution — `DESTROY_CARDS`, `DISCARD_CARDS`,
`REFRESH_CARDS`, `TOSS_CARDS`, `KNOCKOUT_AGENTS`, `ACQUIRE_CARDS`, `REPLACE_CARDS_IN_TAVERN`, `DONATE`,
`ENACT_CHOSEN_EFFECT`, patron-specific `COMPLETE_*` ones), `Type` (`CARD` or `EFFECT`), and the actual
`PossibleCards`/`PossibleEffects` list. You don't need to hand-decode this to play legally (just pick
one of the `MAKE_CHOICE` entries in `possibleMoves`), but it's essential context for scoring which
choice is best.

---

## 4. Game rules data

### Patrons
Full enum (`Engine/src/Patrons/Patron.cs`):

```csharp
public enum PatronId {
    ANSEI = 0, DUKE_OF_CROWS = 1, RAJHIN = 2, PSIJIC = 3, ORGNUM = 4,
    HLAALU = 5, PELIN = 6, RED_EAGLE = 7, TREASURY = 8, SAINT_ALESSIA = 9,
}
```

The engine ships **9 selectable patrons** plus the always-present `TREASURY`; `ScriptsOfTribute.cs`'s
default patron pool (`GetDefaultPatrons`) is all 9 non-Treasury ids, and GameRunner's `--patrons`
(`-p`) flag can restrict which subset is offered (must supply ≥4 if you override). The **current
competition pool (2025/2026)** is the 6-patron subset: `PELIN`, `DUKE_OF_CROWS`, `ANSEI`, `RAJHIN`,
`ORGNUM`, `SAINT_ALESSIA` — 3 of them get drafted per game (plus forced Treasury = 4 patrons total in
play). `HLAALU`, `PSIJIC`, `RED_EAGLE` still exist in the engine (`Patrons/Hlaalu.cs`,
`Patrons/Psijic.cs`, `Patrons/RedEagle.cs`) but are outside the current tournament set — worth checking
`GameRunner --patrons` invocations before assuming a 9-patron pool.

Every `Patron` (`Patrons/Patron.cs`) exposes:
- `PatronID`, `GetStarterCards()` (cards seeded into both players' starting decks whenever this patron
  is in play), `FavoredPlayer` (`PlayerEnum`, starts `NO_PLAYER_SELECTED`),
- `CanPatronBeActivated(Player activator, Player enemy)` — gate checked both for building the legal
  move list and inside the actual activation,
- `PatronActivation(Player activator, Player enemy)` — the one-time effect of spending a patron call;
  returns `(PlayResult, IEnumerable<CompletedAction>)` where `PlayResult` is `Success`, `Failure`
  (should only happen on genuine user error — the legality check should normally prevent this), or
  `Choice` (activation itself needs a follow-up decision),
- `PatronPower(Player activator, Player enemy)` — a passive effect re-applied every turn (only `ANSEI`
  currently has one: +1 coin at start of turn if it favors you).

Favor progression is generally **unfavored → neutral → favored** on activation by one side, and
activation by the *other* side knocks it from favored back to neutral (not to unfavored) — see the
`FavoredPlayer` toggle logic repeated in each patron's `PatronActivation`. `ANSEI` is the stated
exception: activating it always jumps straight to favoring the activator regardless of current state.

The 6 in-pool patrons, mechanically:

| Patron | Cost to activate | Effect (favored / neutral / unfavored) | Starter card | Notes |
|---|---|---|---|---|
| `PELIN` | 2 Power | Same in all favor states: return an Agent from your Cooldown pile to the top of your deck | `FORTIFY` | Produces a `Choice` (which agent) unless cooldown has no agents, in which case not activatable |
| `DUKE_OF_CROWS` | All your Coin | Same in neutral/unfavored: gain Power = Coin paid − 1 (min useful at 2+ coin); **cannot activate again once you already favor him** (`FavoredPlayer != activator.ID` gate) | `PECK` | No distinct "favored" bonus tier described — activating pushes straight to favored and then locks out re-activation by you |
| `ANSEI` | 2 Power | Neutral & unfavored: gain 1 Coin, patron now favors you (jumps straight to favored, the special case above) | `WAY_OF_THE_SWORD` | Passive: +1 Coin at start of your turn while favored |
| `RAJHIN` | 3 Coin | Same in all states: put a `BEWILDERMENT` (dead/useless) card into the **opponent's** cooldown pile | `SWIPE` | Pure disruption — dilutes opponent's future draws |
| `ORGNUM` | 3 Coin | Favored: +1 Power per 4 cards you own (rounded down) *and* add a `SUMMERSET_SACKING` card to your cooldown; Neutral: +1 Power per 6 cards you own; Unfavored: flat +2 Power | `SEA_ELF_RAID` | Reward scales with total card pool size (`GetAllPlayersCards()` = hand+draw+played+agents+cooldown) |
| `SAINT_ALESSIA` | 4 Coin | Favored: create `CHAINBREAKER_SERGEANT` in cooldown; Neutral: create `SOLDIER_OF_THE_EMPIRE` in cooldown; Unfavored: flat +2 Power | `ALESSIAN_REBEL` | Escalating value the more favored you are |

### Win conditions
`BoardManager.cs::CheckAndGetWinner()`, called **after every `EndTurn()`** (so mid-turn state can
exceed thresholds without ending the game — the check point is turn boundaries only). At the check
point, "CurrentPlayer" means the player whose turn is *starting* and "EnemyPlayer" is the one who just
finished:

1. **Prestige ≥ 80 → instant win.** If the player who just ended their turn (`EnemyPlayer` at check
   time) has `PrestigeAmount >= 80`, they win immediately (`GameEndReason.PRESTIGE_OVER_80`) — no
   "defend" window.
2. **4 patron favors → instant win.** If every non-Treasury patron in play favors the player who just
   ended their turn, that's an immediate win (`GameEndReason.PATRON_FAVOR`). With the 6-patron pool
   only 3 patrons are drafted per game, so "4 favors" in practice means all 3 drafted patrons plus...
   Treasury is explicitly excluded from this check (`patron.PatronID != PatronId.TREASURY`), so with 3
   drafted patrons the maximum obtainable is 3 — this win condition requires whatever patron count the
   match was configured with to allow 4 non-Treasury patrons in play (i.e. it's reachable in a
   default/9-patron-pool game with 4 patrons drafted, not a guaranteed reachable line in every
   configuration). Check your `--patrons` configuration if this matters for your bot's planning.
3. **Prestige ≥ 40, unmatched → win at opponent's next turn boundary ("defend").** The check runs
   *after* the turn-ending swap, so at check time `CurrentPlayer` means "the player whose turn is about
   to start" and `EnemyPlayer` means "the player who just finished their turn." The rule:
   if `CurrentPlayer.Prestige >= 40` **and** `EnemyPlayer.Prestige < CurrentPlayer.Prestige`, then
   `CurrentPlayer` wins (`GameEndReason.PRESTIGE_OVER_40_NOT_MATCHED`) — immediately, before
   `CurrentPlayer` even gets to act. Concretely: you cross 40 prestige and end your turn; the check
   right then only compares your opponent (who hasn't moved yet) against you, so it can't trigger yet.
   Your opponent then plays their entire turn. When *they* end their turn, the check runs again — now
   *you* are `CurrentPlayer` (about to move next) — and if your prestige is still ≥40 and still
   strictly greater than theirs (they failed to match or exceed it during their one response turn), you
   win right there. So crossing 40 grants your opponent exactly one full turn to either match/exceed
   your prestige or end the game some other way (80-instant-win, 4-favor win); failing that, you win at
   the start of your following turn.

Order of checks matters: 80-prestige is checked first, then patron-favor sweep, then the 40-threshold
defend logic — so simultaneous conditions resolve in that priority order.

Other terminal states, not "wins" in the strategic sense but still `EndGameState` outcomes your bot
should expect to both cause and see caused against it (`GameEndReason` enum,
`Engine/src/Board/GameEndReason.cs`): `INCORRECT_MOVE`, `TURN_TIMEOUT`, `PATRON_SELECTION_TIMEOUT`,
`PATRON_SELECTION_FAILURE`, `TURN_LIMIT_EXCEEDED` (game auto-ends as a draw-ish no-winner state past
`ScriptsOfTributeGame.TurnLimit = 500` turns, winner = `NO_PLAYER_SELECTED`), `INTERNAL_ERROR`,
`BOT_EXCEPTION` (your `Play`/`SelectPatron` threw — you lose, opponent wins), `PREPARE_TIME_EXCEEDED`.

### Combo system
`Board/CardAction/ComboContext.cs`. Each card has up to 4 `Effects[]` slots: `Effects[0]` = base
on-play effect, `Effects[1..3]` = combo tiers 2/3/4, keyed per-`PatronId` (i.e. combo count is tracked
**per patron/deck**, not globally) — `Card.cs`: "0th - On activation, 1st - combo 2, 2nd - combo 3, 3rd
- combo 4". Playing a card of a given patron's deck increments that patron's `Combo.ComboCounter` (capped
at `Combo.MAX_COMBO = 4`) and queues that combo tier's effect (if the card defines one) into an
`EffectQueue`; `GetCurrentComboEffects()` then drains and returns every queued effect up to the current
counter. Effects whose `EffectType == OPP_DISCARD` are special-cased into `StartOfNextTurnEffects`
(applied at the very top of the opponent's next turn, producing `START_OF_TURN_CHOICE_PENDING` if a
choice results) rather than resolved immediately. Combo counters reset every turn (`ComboContext.Reset()`
is called from `CardActionManager.Reset`, invoked on turn swap).

### Agents and taunt
`Board/Cards/Agent.cs` / `Player.cs`. Playing an `AGENT`-type card (or buying a `CONTRACT_AGENT`) puts
a live `Agent` wrapper (tracks `CurrentHp`, starts at the card's `HP`, and `Activated` state) onto your
board instead of into `Played`. Agents start `Activated = true` when created via `PlayCard`/`BuyCard`
(so they can't immediately tap again the turn they enter) and refresh (`Activated = false`) at the
start of your next turn (`Player.EndTurn` calls `Agents.ForEach(agent => agent.Refresh())`). `ATTACK`
moves against an agent deal `min(agent.CurrentHp, yourPower)` damage and consume that much `Power`; an
agent at `CurrentHp <= 0` dies — normal `AGENT` cards go to your cooldown pile, `CONTRACT_AGENT` cards
(bought from tavern) return to the tavern's discard (`Tavern.Cards`), not your pile.

**Taunt** (`Card.Taunt`, `bool`) restricts targeting: while computing legal moves,
`GetListOfPossibleMoves` only offers `ATTACK` against the opponent's taunt agents if any exist on their
board — non-taunt agents become untargetable until all taunt agents are cleared. Separately, at
`EndTurn`, any of the *opponent's* taunt agents force an automatic attack from the player ending their
turn (`BoardManager.EndTurn`: `foreach (var agent in agentsWithTaunt) CurrentPlayer.AttackAgent(...)`)
— i.e. taunt agents get a "free hit" against whoever is about to pass the turn to them, before the
prestige/coin/power reset happens.

### Tavern
`Board/Tavern.cs`. `AvailableCards` is the 5 face-up purchasable slots; `Cards` is the shuffled
face-down deck backing it. Buying (`BuyCard`) removes the card from `AvailableCards` at its index and
immediately redraws from `Cards` into the same slot (`Tavern.DrawAt`) — so slot position is preserved
across a buy but the deck composition shrinks. `CONTRACT_AGENT`/`CONTRACT_ACTION` cards you buy don't
go straight into your deck like normal purchases — `CONTRACT_AGENT` becomes an immediately-activated
agent on your board, `CONTRACT_ACTION` gets added back into the tavern's own discard pool
(`Tavern.Cards.Add(card)`) rather than your pile, per `BoardManager.BuyCard`. Regular (non-contract)
purchases go to your `CooldownPile` (i.e. they'll shuffle into your deck, not your hand, the next time
your cooldown reshuffles in).

---

## 5. Timeouts and how GameRunner enforces them

**There is one timer per turn, not per move, and not one flat per-game timer.** It's the
`TimeSpan Timeout` you construct `ScriptsOfTribute` with (`ScriptsOfTribute.cs`), defaulting to 30s if
unspecified, or GameRunner's `--timeout`/`-to <seconds>` flag (default `30`). Internally
(`ScriptsOfTributeGame.cs`):

```csharp
private TimeSpan _currentTurnTimeElapsed = TimeSpan.Zero;
private TimeSpan CurrentTurnTimeRemaining => _timeout - _currentTurnTimeElapsed;
```

Every call into your bot (`Play` for a normal move, or `Play` again to answer a pending choice) runs
inside `MoveTask`, which measures wall-clock elapsed with a `Stopwatch` and **accumulates** it into
`_currentTurnTimeElapsed` regardless of outcome. `_currentTurnTimeElapsed` only resets to zero when
`HandleEndTurn()` runs, i.e. once you've actually returned an `END_TURN` move and it's been accepted.
So: if your turn involves playing 4 cards and resolving 2 choices before ending turn, all 6 `Play`
calls draw from the same 30-second pool, and `remainingTime` handed to each call reflects what's left
after every prior call in that turn (including ones spent resolving choices you triggered).

Enforcement mechanism (`PlayWithTimeout`): your `Play` call is wrapped in `Task.Run(...)` and awaited
with `task.Wait(CurrentTurnTimeRemaining)`. **What happens exactly at expiry:**
- `task.Wait` returns `false` (times out) → the method returns
  `(new EndGameState(_api.EnemyPlayerId, GameEndReason.TURN_TIMEOUT), null)`.
- The game loop immediately calls `EndGame(...)` with that state — **the game ends right there**, no
  further moves from anyone. The timed-out player's opponent (`EnemyPlayerId` relative to the timed-out
  player, i.e. the *other* player) is declared the winner. This is a full forfeit, not a "skip this
  move" — one late move loses the entire match.
- **Important subtlety:** timing out the `Task.Wait` does **not** cancel the underlying `Task` — your
  `Play` implementation, if it's stuck in a long-running loop, keeps executing on a thread-pool thread
  in the background even after the game has already ended and moved on. In multi-threaded GameRunner
  batches (`-t <threads>`) this is a real resource-leak risk: a bot that occasionally hangs can pile up
  zombie background threads across many games in a run. Design your `Play` to **check elapsed time
  itself** and bail out well before `remainingTime` hits zero (see MCTSBot pattern below), rather than
  relying on the engine to stop you.
- Any exception thrown out of `Play`/`SelectPatron` (uncaught) surfaces as an `AggregateException` from
  the awaited `Task`; the engine catches it, logs `Message`+`StackTrace`, and ends the game with
  `GameEndReason.BOT_EXCEPTION`, opponent wins. So an unhandled exception is exactly as fatal as a
  timeout — don't let simulation code throw on edge cases (empty hand, no legal targets, etc.).

**Patron-selection timeout** uses the identical `Timeout` value independently per `SelectPatron` call
(`SelectPatronWithTimeout` in `ScriptsOfTribute.cs`) — it is *not* shared with turn-move time. A
patron-selection timeout also ends the game outright (`GameEndReason.PATRON_SELECTION_TIMEOUT`), before
any board even exists.

**Pregame prepare** has its own separate, non-configurable 5-second budget (`PrepareBots`, hardcoded
`timeout = 5` in `ScriptsOfTribute.cs::Play()`), unaffected by `--timeout`.

**Memory** — there is **no in-engine memory limiter**. Nothing in `ScriptsOfTributeGame`, `GameRunner`,
or the `Dockerfile` caps or measures your bot's memory footprint; the shipped `Dockerfile` just
installs the .NET 7 SDK/runtime on Ubuntu with no cgroup/ulimit configuration. Any memory ceiling is an
external competition-infrastructure concern (e.g. a Docker `--memory` flag applied by the tournament
runner outside this repo), not something you can query or that will gracefully disqualify you from
inside the engine — an out-of-memory bot will just crash/hang and most likely manifest as a
`TURN_TIMEOUT` or `BOT_EXCEPTION` loss instead of a distinct "memory" reason. Budget accordingly if
you're keeping large transposition tables or MCTS trees alive across the 30s turn budget × up to 500
turns × however many parallel GameRunner threads you configure.

There is no default value baked in for "10 seconds" anywhere in the current code — the GameRunner
`--timeout` default is **30 seconds** (`CreateOption<int>("--timeout", ..., 30, "-to")`); if a
competition ruleset specifies a stricter 10s per-turn budget, that's set via `-to 10` on the GameRunner
invocation, and everything above about accumulation-within-a-turn / instant-forfeit-on-expiry applies
identically regardless of the configured number.

---

## 6. Performance notes for search bots

- **`ApplyMove` is not cheap.** Every call goes `FullGameState.ApplyMove` →
  `ScriptsOfTributeApi.FromSerializedBoard(this)` → `BoardManager.FromSerializedBoard`, which
  reconstructs `Patron[]` (via `Patron.FromSerializedBoard`, re-instantiating a fresh `Patron` object
  per patron in play), a fresh `Tavern`, a fresh `PlayerContext`/`Player` pair (deep-copying `Hand`,
  `DrawPile`, `Played`, `CooldownPile`, `Agents` into new `List<T>`s via `Player.FromSerializedPlayer`),
  and a fresh `CardActionManager` (replaying `ComboContext`/`ExecutionChain`/`CompletedActions` state)
  — then applies the one move, then serializes the whole thing straight back out again
  (`GetFullGameState()` + `GetListOfPossibleMoves()`, itself an O(hand+agents+tavern+…) scan, worse if
  a choice with combinatorial `MAKE_CHOICE` expansion is active). There is **no incremental/undo API** —
  every simulated ply is a full clone-and-replay, not a cheap in-place mutation.
- Corollary: a rollout of depth *d* costs roughly *d* full board reconstructions, and a turn can require
  many plies (multiple card plays + attacks + buys before `END_TURN`). Budget your search accordingly —
  this is why the shipped `MCTSBot`/`BeamSearchBot` cap themselves to a small time slice per *move*
  (`timeForMoveComputation = 0.3s` in `MCTSBot`) rather than trying to exhaust the 30s turn budget on a
  single search.
- **`GetListOfPossibleMoves()` inside `ApplyMove`'s return, and `IsMoveLegal`, both re-scan from
  scratch** — don't call `IsMoveLegal` speculatively in a simulation loop; you already get the correct
  legal-move list back from every `ApplyMove`/`Play` call for free.
- **`SeededRandom` is a cheap 48-bit LCG** (`Engine/src/utils/SeededRandom.cs`, Java `Random`-style
  linear congruential generator, one multiply+add+shift per `Next()`) — fine to call heavily inside
  rollouts; it is not cryptographically random and is fully deterministic given a seed, which is exactly
  what you want for reproducible simulation.
- **`MAKE_CHOICE` combinatorics can dominate branching factor.** `GetCombinations<T>(k)` is exponential
  in `min(k, n-k)`; a choice like "discard any number of cards, 0 to 5" generates `2^5 = 32` distinct
  moves for that single decision point, `C(n,k)` for a fixed-k choice. If your search treats every
  `MAKE_CHOICE` branch as a normal decision node, choice-heavy cards can blow up your effective branching
  factor far more than normal `PLAY_CARD`/`ATTACK`/`BUY_CARD` decisions do — worth special-casing
  (e.g. greedy/heuristic choice resolution instead of full search) if you hit this.
- **Reuse a fixed bot-owned seed for simulation determinism, but re-randomize periodically to sample
  different hidden-info worlds.** Both example bots hardcode one seed for their whole simulation
  lifetime (`MCTSBot`: `botSeed = 42`; `BeamSearchBot`: passes `123` into `ToSeededGameState`) — this
  makes a single search tree internally consistent (the same "guessed" opponent hand throughout one
  tree), but if you want an ISMCTS-style average over hidden-info worlds you need to explicitly vary the
  seed across independent tree builds/rollouts yourself; nothing in the engine does this averaging for
  you.
- **The `AI` instance is not recreated between games within one GameRunner run.** `RunSingleThreaded`/
  `RunMultiThreaded` call `bot1Info.CreateBotInstance()` **once** before the `for` loop over `-n` runs,
  then reuse the same object across every game. Any bot-level mutable state (trees, logs, "startOfGame"
  flags like the shipped bots use) must be reset in `GameEnd`/at the top of `Play`/`SelectPatron` if it
  shouldn't leak across games — see `MCTSBot.PrepareForGame()` and the `startOfGame` flags in
  `MaxPrestigeBot`/`BeamSearchBot` for the pattern.
- Multi-threaded GameRunner (`-t N`) runs one bot instance **per thread**, each playing its own
  sub-sequence of games sequentially — so your bot doesn't need to be thread-safe across concurrent
  games, only correct in the reused-instance-across-sequential-games sense above.

---

## 7. Adding a bot and running it

### Add a bot class
1. Add a new `.cs` file under `Bots/src/`, e.g. `Bots/src/MyBot.cs`, in the `Bots` namespace, deriving
   from `ScriptsOfTribute.AI.AI`, implementing `SelectPatron`/`Play`/`GameEnd` (see §1). It **must**
   have a public parameterless constructor — `GameRunner` checks this explicitly
   (`BotInfo.cs`/`Program.cs`: `cachedBot.BotType.GetConstructor(Type.EmptyTypes) is null` → error).
2. Build: from `ScriptsOfTribute-Core/Bots`, run `dotnet build`. This produces a DLL that both
   `GameRunner` and any GUI app load by scanning a `Bots` directory next to the runner executable for
   `*.dll` files and reflecting over them for types assignable to `AI` (`GameRunner/Program.cs`, top of
   file) — no manual registration step, just build and the class is discoverable by name.
3. If two loaded DLLs define a bot with the same short class name, GameRunner will refuse to
   disambiguate by short name and asks you to pass the fully-qualified `<namespace>.<ClassName>`
   instead (`FindInternalBot`).

### Run it
From the built `GameRunner`:

```
GameRunner <bot1> <bot2> [flags]

# examples
GameRunner RandomBot RandomBot -n 1000 -t 4          # 1000 games, 4 threads
GameRunner MyBot MCTSBot -n 20 -to 10 -s 12345        # 10s/turn timeout, fixed RNG seed
GameRunner MyBot grpc:SomePythonBot -n 100            # vs. a Python bot over gRPC
GameRunner MyBot cmd:"python3 my_stdin_bot.py"        # vs. the deprecated stdin/stdout bridge
```

Key flags (`GameRunner/Program.cs`):

| Flag | Alias | Default | Meaning |
|---|---|---|---|
| `--runs` | `-n` | 1 | Number of games |
| `--threads` | `-t` | 1 | Parallel worker threads (games split roughly evenly) |
| `--timeout` | `-to` | 30 | Per-turn timeout in seconds (§5) |
| `--seed` | `-s` | random | Initial RNG seed; each subsequent game in the run increments it by 1 |
| `--patrons` | `-p` | all 9 | Space/comma-separated patron pool to draft from; `default`/`all` = full pool; custom pool needs ≥4 entries |
| `--enable-logs` | `-l` | `NONE` | `P1`/`P2`/`BOTH`/`NONE` — turns on `Log()` output capture |
| `--log-destination` | `-d` | stdout | Directory to write `<seed>_<bot>.log` files instead of console |
| `--client-port` / `--server-port` | `-cp`/`-sp` | 50000/49000 | Base ports for gRPC bots (auto-offset per thread) |

Run `GameRunner -h` for the live flag list. Output includes per-run and average game time in ms and a
summary of `GameEndReason` counts (`GameEndStatsCounter`) — useful for sanity-checking your bot isn't
losing to `TURN_TIMEOUT`/`BOT_EXCEPTION`/`INCORRECT_MOVE` instead of on merits.

### External-language bots
- **gRPC (supported):** run your bot as a separate process using the `scripts-of-tribute` Python
  package (or implement the same gRPC contract in another language — see `gRPC/Services/AIService.cs`
  / `gRPC/gRPCBot.cs`), then reference it in GameRunner as `grpc:<BotName>`.
- **stdin/stdout adapter (obsolete):** `ExternalAIAdapter` pipes newline-delimited state to a spawned
  process and reads back a move index; kept for backward compatibility only, not recommended for new
  bots per the README.
