#!/usr/bin/env python3
"""
replay.py — turn a logged Tales of Tribute bot-vs-bot game into a single
self-contained, offline HTML replay viewer (Kaggle-style step-through).

USAGE
    python3 scripts/replay.py <logdir-or-files> -o results/replay.html
    python3 scripts/replay.py results/replay-sample -o results/replay-demo.html

    <logdir-or-files> is either:
      - a directory containing a `<seed>_trace.json` file (preferred, see
        "LOG FORMAT FINDINGS" below), and/or GameRunner's `<seed>_p1.log` /
        `<seed>_p2.log` files, or
      - one or more explicit file paths (a .json trace, and/or .log files).

    Optional: --seed N, --bot1 NAME, --bot2 NAME to label a game when they
    can't be inferred (only matters in raw-log-only fallback mode; the JSON
    trace already embeds bot names/seed).

================================================================================
LOG FORMAT FINDINGS (read this before assuming GameRunner's `-l` logs are
enough — they usually are NOT, for competitive bots)
================================================================================

GameRunner's `-l BOTH -d <dir>` writes `<seed>_p1.log` / `<seed>_p2.log`.
Looking at the engine source (ScriptsOfTribute-Core/Engine/src/AI/AI.cs,
ScriptsOfTributeGame.cs, utils/Logger.cs): these files contain *only* text a
bot explicitly hands to its own protected `Log(string)` method. The engine
itself never auto-logs moves, state, or anything else — Logger.Log() just
drains whatever's in `AI.LogMessages` after each Play()/SelectPatron() call.

Verified empirically: running `Riptide` vs `BestMCTS3` (seed 12345, see
results/replay-sample/12345_p1.log / 12345_p2.log) produces two 0-byte log
files, because neither bot calls Log() on the normal move path (Riptide only
logs on an internal exception-recovery path; BestMCTS3 never calls it at
all). Logging is opt-in per bot — e.g. MaxPrestigeBot in this repo *does*
call `Log(move.ToString())` every turn, so its logs would actually contain a
move-by-move trace in the `[PLAYERn][hh:mm:ss:fff][turn][move] MOVE ...`
format described in utils/Logger.cs. But that's a property of the bot, not
of GameRunner or the log flag.

So for the demo pair requested (Riptide vs BestMCTS3), the standard
GameRunner logs carry ~no usable state. Per the task's own escape hatch
("reconstruct state by parsing what's there... don't fabricate"), this
script does the next best faithful thing instead of inventing data:

A small standalone harness (not part of this repo — a throwaway dev tool
built in the session scratchpad, never committed) wraps each bot's public
`AI.Play()/SelectPatron()/GameEnd()` calls with a decorator and, on every
call, captures `GameState.SerializeGameState()` — a JSON dump the engine
*already builds internally* (Engine/src/Serializers/GameState.cs) containing
patron favor state, both players' Coins/Power/Prestige/Agents (all public
information — SerializedPlayer exposes these regardless of viewpoint; only
exact hidden-hand *card identities* of the non-mover are folded into a
combined HandAndDraw bag, per the engine's own "fair view" design — see
docs/engine-api.md §2), the shared tavern, and the cumulative
`CompletedActions` log (human-decodable via CompletedAction.SimpleString(),
Engine/src/Board/CompletedAction.cs). This is genuine engine-computed state,
not fabricated — it only required hooking the *public* AI interface that
GameRunner itself uses, not modifying engine or bot source.

That harness's output is the `<seed>_trace.json` this script actually reads.
results/replay-sample/ contains one such trace (Riptide vs BestMCTS3, seed
12345) alongside the (empty, for-the-record) raw GameRunner logs.

If no `*_trace.json` is found, this script falls back to a best-effort
parse of the raw `_p1.log` / `_p2.log` text (useful for bots like
MaxPrestigeBot that do call Log() per move): it extracts whatever
`[PLAYERn][turn][move] message` lines exist and shows them as a bare
message timeline WITHOUT resource/board reconstruction, and says so in the
viewer — because that state genuinely isn't recoverable from text logs
alone. It does not invent Coins/Power/Prestige/agent data in that mode.

================================================================================
KNOWN LIMITATIONS / APPROXIMATIONS (also shown in the generated HTML footer)
================================================================================
- Exact hidden hand-card identities of the player NOT currently acting are
  not shown (engine's own "fair view" folds them into an unordered bag) —
  this only affects hand contents, which the spec doesn't require; agents in
  play, resources, and patron favor are always fully public and shown exactly.
- "Whose turn" is derived from which player the engine actually asked to
  move at that step (ground truth), not a separately-tracked turn counter;
  turn number is inferred by counting END_TURN moves seen so far.
- The per-step "effects" list is a light regex re-format of the engine's
  CompletedAction.SimpleString() output, not a hand-authored description —
  unrecognized action types fall back to a generic Title Case rendering
  rather than being dropped or invented.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
from pathlib import Path

# --------------------------------------------------------------------------
# Locating input
# --------------------------------------------------------------------------

def find_inputs(raw_args: list[str]) -> tuple[Path | None, list[Path]]:
    """Returns (trace_json_path_or_None, list_of_log_paths)."""
    paths: list[Path] = []
    for a in raw_args:
        p = Path(a)
        if p.is_dir():
            paths.extend(sorted(p.glob("*_trace.json")))
            paths.extend(sorted(p.glob("*.log")))
        elif p.is_file():
            paths.append(p)
        else:
            print(f"warning: input path does not exist: {p}", file=sys.stderr)

    trace_candidates = [p for p in paths if p.suffix == ".json"]
    log_candidates = [p for p in paths if p.suffix == ".log"]

    trace_path = None
    if trace_candidates:
        if len(trace_candidates) > 1:
            print(
                f"note: multiple trace JSONs found, using the first: {trace_candidates[0]}",
                file=sys.stderr,
            )
        trace_path = trace_candidates[0]

    return trace_path, log_candidates


# --------------------------------------------------------------------------
# Raw-log-only fallback (best effort, no fabrication)
# --------------------------------------------------------------------------

LOG_LINE_RE = re.compile(
    r"^\[(?P<player>PLAYER[12])\]\[(?P<time>[\d:]+)\]\[(?P<turn>\d+)\]\[(?P<move>\d+)\] (?P<msg>.*)$"
)


def parse_raw_logs(log_paths: list[Path], bot1: str, bot2: str, seed: str) -> dict:
    messages = []
    for p in log_paths:
        text = p.read_text(errors="replace")
        for line in text.splitlines():
            m = LOG_LINE_RE.match(line)
            if m:
                messages.append(
                    {
                        "player": m.group("player"),
                        "turn": int(m.group("turn")),
                        "move": int(m.group("move")),
                        "msg": m.group("msg"),
                    }
                )
            elif line.strip():
                # Non-standard line (e.g. bare Log(msg) with no timestamp/turn
                # prefix, written before the first Play() call). Keep it, but
                # don't pretend we know the turn/move index.
                messages.append({"player": None, "turn": None, "move": None, "msg": line.strip()})

    return {
        "mode": "raw-log-fallback",
        "meta": {"bot1": bot1, "bot2": bot2, "seed": seed},
        "messages": messages,
    }


# --------------------------------------------------------------------------
# JSON trace -> compact viewer data
# --------------------------------------------------------------------------

def build_uid_registry(raw: dict) -> dict[str, dict]:
    """Walk the whole trace collecting {UniqueId(str): {name, deck, cost, type}}."""
    registry: dict[str, dict] = {}

    def visit(node):
        if isinstance(node, dict):
            if "UniqueId" in node and "name" in node:
                registry[str(node["UniqueId"])] = {
                    "name": node.get("name"),
                    "deck": node.get("deck"),
                    "cost": node.get("cost"),
                    "type": node.get("type"),
                }
            for v in node.values():
                visit(v)
        elif isinstance(node, list):
            for v in node:
                visit(v)

    visit(raw.get("steps"))
    if raw.get("finalState"):
        visit(raw["finalState"])
    return registry


def prettify_token(tok: str) -> str:
    return tok.replace("_", " ").strip().title()


CARD_BLOCK_RE = re.compile(
    r"Card: (?P<name>[^,]+), Deck: (?P<deck>\w+), Cost: (?P<cost>-?\d+), Type: (?P<type>\w+), UniqueId: (?P<uid>\d+)"
)

ACTION_VERBS = {
    "BUY_CARD": ("bought", "target"),
    "ACQUIRE_CARD": ("acquired", "target"),
    "PLAY_CARD": ("played", "target"),
    "ACTIVATE_AGENT": ("activated agent", "target"),
    "ACTIVATE_PATRON": ("called patron", "patron"),
    "ATTACK_AGENT": ("attacked", "target_amount"),
    "AGENT_DEATH": ("agent died:", "target"),
    "GAIN_COIN": ("+{amt} Coin", "source_amount"),
    "GAIN_POWER": ("+{amt} Power", "source_amount"),
    "GAIN_PRESTIGE": ("+{amt} Prestige", "source_amount"),
    "OPP_LOSE_PRESTIGE": ("opponent -{amt} Prestige", "source_amount"),
    "REPLACE_TAVERN": ("replaced tavern card with", "source_target"),
    "DESTROY_CARD": ("destroyed", "source_target"),
    "DRAW": ("drew {amt} card(s)", "source_amount"),
    "DISCARD": ("discarded", "source_target"),
    "REFRESH": ("refreshed", "source_target"),
    "TOSS": ("tossed", "source_target"),
    "KNOCKOUT": ("knocked out", "source_target"),
    "KNOCKOUT_ALL": ("knocked out all copies of", "target"),
    "ADD_PATRON_CALLS": ("+{amt} patron call(s)", "source_amount"),
    "ADD_WRIT_OF_COIN": ("gained a Writ of Coin", "source_only"),
    "ADD_SUMMERSET_SACKING": ("gained a Summerset Sacking", "source_only"),
    "ADD_BEWILDERMENT_TO_OPPONENT": ("gave opponent a Bewilderment", "source_only"),
    "ADD_CHAINBREAKER_SERGEANT": ("gained a Chainbreaker Sergeant", "source_only"),
    "ADD_SOLDIER_OF_THE_EMPIRE": ("gained a Soldier of the Empire", "source_only"),
    "HEAL_AGENT": ("healed {amt}", "source_target_amount"),
    "END_TURN": ("ended turn", "none"),
}


def parse_completed_action(raw_line: str, patron_names: set[str]) -> str:
    cards = [m.groupdict() for m in CARD_BLOCK_RE.finditer(raw_line)]
    remainder = CARD_BLOCK_RE.sub("", raw_line).strip()
    parts = remainder.split()
    action_type = parts[0] if parts else raw_line.strip()
    rest_tokens = parts[1:]

    patron = None
    amount = None
    for t in rest_tokens:
        if t in patron_names:
            patron = t
        elif re.fullmatch(r"-?\d+", t):
            amount = t

    verb, shape = ACTION_VERBS.get(action_type, (None, None))
    source = cards[0]["name"] if len(cards) >= 1 else None
    target = cards[-1]["name"] if len(cards) >= 1 else None
    src_label = source if source else (prettify_token(patron) if patron else None)

    if verb is None:
        # Unknown action type: faithful generic fallback, no invention.
        bits = [prettify_token(action_type)]
        if src_label:
            bits.append(src_label)
        if len(cards) == 2:
            bits.append("->")
            bits.append(target)
        if amount:
            bits.append(f"({amount})")
        return " ".join(bits)

    if shape == "target":
        return f"{verb} {target}" if target else verb
    if shape == "patron":
        return f"{verb} {prettify_token(patron)}" if patron else verb
    if shape == "target_amount":
        return f"{verb} {target} for {amount}" if target else verb
    if shape == "source_amount":
        # verb templates like "+{amt} Coin" assume a positive amount; some
        # actions (e.g. Orgnum's coin cost) legitimately carry a negative
        # Amount, so don't double up the sign ("+-3").
        amt_str = amount or "?"
        if amt_str.startswith("-") and "+{amt}" in verb:
            label = verb.replace("+{amt}", "-{amt}").format(amt=amt_str[1:])
        else:
            label = verb.format(amt=amt_str)
        return f"{label} ({src_label})" if src_label else label
    if shape == "source_target":
        if src_label and target and src_label != target:
            return f"{verb} {target} (via {src_label})"
        return f"{verb} {target or src_label or ''}".strip()
    if shape == "source_target_amount":
        label = verb.format(amt=amount or "?")
        return f"{label} {target} (via {src_label})" if src_label else f"{label} {target}"
    if shape == "source_only":
        return f"{verb} (via {src_label})" if src_label else verb
    if shape == "none":
        return verb
    return remainder or action_type


MOVE_VERBS = {
    "PLAY_CARD": "played",
    "BUY_CARD": "bought",
    "ACTIVATE_AGENT": "activated",
    "ATTACK": "attacked",
}


def build_headline(move_str: str, move_json: str, registry: dict[str, dict]) -> str:
    parts = move_json.split(" ", 1)
    cmd = parts[0]
    rest = parts[1].strip() if len(parts) > 1 else ""

    if cmd in MOVE_VERBS:
        uid = rest.strip()
        card = registry.get(uid)
        if card:
            name = card["name"]
        else:
            # fall back to the CommonId in move_str, e.g. "PLAY_CARD GOLD"
            fallback_parts = move_str.split(" ", 1)
            name = prettify_token(fallback_parts[1]) if len(fallback_parts) > 1 else "?"
        return f"{MOVE_VERBS[cmd]} {name}"
    if cmd == "PATRON":
        return f"called patron {prettify_token(rest)}"
    if cmd == "CHOICE":
        if rest:
            names = ", ".join(prettify_token(n) for n in rest.split())
            return f"chose {names}"
        return "made a choice"
    if cmd == "END_TURN":
        return "ended turn"
    return move_str


def normalize_player_block(block: dict) -> dict:
    agents = [
        {
            "name": a["Card"]["name"],
            "hp": a["CurrentHP"],
            "maxHp": a["Card"].get("HP"),
            "activated": a.get("Activated", False),
            "taunt": a["Card"].get("taunt", False),
            "deck": a["Card"].get("deck"),
        }
        for a in block.get("Agents", [])
    ]
    return {
        "player": block["Player"],
        "coins": block.get("Coins", 0),
        "power": block.get("Power", 0),
        "prestige": block.get("Prestige", 0),
        "agents": agents,
    }


def compact_trace(raw: dict) -> dict:
    registry = build_uid_registry(raw)
    patron_names = set()
    if raw["steps"]:
        patron_names = set(raw["steps"][0]["state"]["PatronStates"].keys())
    elif raw.get("finalState"):
        patron_names = set(raw["finalState"]["PatronStates"].keys())

    steps = []

    # --- synthetic "patron draft" steps ---
    known_patrons: dict[str, str] = {p: "NONE" for p in patron_names}
    for pick in raw.get("patronPicks", []):
        known_patrons[pick["patron"]] = "P1" if pick["player"] == "PLAYER1" else "P2"
        steps.append(
            {
                "phase": "setup",
                "turn": 0,
                "mover": "P1" if pick["player"] == "PLAYER1" else "P2",
                "headline": f"drafted patron {prettify_token(pick['patron'])} (round {pick['round']})",
                "effects": [],
                "p1": {"coins": 0, "power": 0, "prestige": 0, "agents": []},
                "p2": {"coins": 0, "power": 0, "prestige": 0, "agents": []},
                "patrons": dict(known_patrons),
                "tavern": [],
            }
        )

    # --- main play steps ---
    # IMPORTANT: `state` in a given raw step is a live GameState built right
    # BEFORE that step's move is applied (docs/engine-api.md §1: "gameState is
    # a fresh GameState object built from the live board every single call").
    # So both the resource/board numbers AND the CompletedActions log in
    # raw_steps[i] describe the world *before* move i, not after. To make each
    # displayed step read naturally as "<mover> did X" followed by the
    # resulting board, we pair move i's headline with the *next* snapshot
    # (raw_steps[i+1], or finalState for the last move) for everything -
    # resources, agents, patrons, tavern, and the CompletedActions delta. This
    # was caught empirically: without the shift, "played Gold" was shown next
    # to the *previous* move's "+1 Coin" and pre-move resource counts.
    raw_steps = raw["steps"]
    completed_lens = [len(s["state"].get("CompletedActions", [])) for s in raw_steps]
    after_states = [raw_steps[i + 1]["state"] if i + 1 < len(raw_steps) else raw.get("finalState") for i in range(len(raw_steps))]
    if raw.get("finalState"):
        full_actions = raw["finalState"].get("CompletedActions", [])
    elif raw_steps:
        full_actions = raw_steps[-1]["state"].get("CompletedActions", [])
    else:
        full_actions = []

    turn = 1
    for i, raw_step in enumerate(raw_steps):
        after = after_states[i] or raw_step["state"]  # fall back to pre-state if nothing better exists
        mover = "P1" if raw_step["mover"] == "PLAYER1" else "P2"

        cp = normalize_player_block(after["CurrentPlayer"])
        ep = normalize_player_block(after["EnemyPlayer"])
        by_player = {cp["player"]: cp, ep["player"]: ep}
        p1 = by_player.get("PLAYER1", cp)
        p2 = by_player.get("PLAYER2", ep)

        patrons = {k: ("P1" if v == "PLAYER1" else "P2" if v == "PLAYER2" else "NONE") for k, v in after["PatronStates"].items()}

        start = completed_lens[i]
        end = completed_lens[i + 1] if i + 1 < len(completed_lens) else len(full_actions)
        delta = full_actions[start:end]
        headline = build_headline(raw_step["move"], raw_step["moveJson"], registry)
        # The CompletedActions log records the triggering move itself too
        # (e.g. PLAY_CARD), which just restates the headline - drop exact
        # echoes so "effects" only shows genuinely new information (resource
        # gains, deaths, draws, etc.) caused by the move.
        effects = [
            parsed for line in delta
            if (parsed := parse_completed_action(line, patron_names)).lower() != headline.lower()
        ]

        tavern = [
            {"name": c["name"], "cost": c["cost"], "deck": c["deck"]}
            for c in after.get("TavernAvailableCards", [])
        ]

        steps.append(
            {
                "phase": "play",
                "turn": turn,
                "mover": mover,
                "headline": headline,
                "effects": effects,
                "p1": {"coins": p1["coins"], "power": p1["power"], "prestige": p1["prestige"], "agents": p1["agents"]},
                "p2": {"coins": p2["coins"], "power": p2["power"], "prestige": p2["prestige"], "agents": p2["agents"]},
                "patrons": patrons,
                "tavern": tavern,
            }
        )

        if raw_step["moveJson"].startswith("END_TURN"):
            turn += 1

    # --- final "game over" step ---
    result = raw.get("result", {})
    if raw.get("finalState"):
        fs = raw["finalState"]
        cp = normalize_player_block(fs["CurrentPlayer"])
        ep = normalize_player_block(fs["EnemyPlayer"])
        by_player = {cp["player"]: cp, ep["player"]: ep}
        p1 = by_player.get("PLAYER1", cp)
        p2 = by_player.get("PLAYER2", ep)
        patrons = {k: ("P1" if v == "PLAYER1" else "P2" if v == "PLAYER2" else "NONE") for k, v in fs["PatronStates"].items()}
        winner = result.get("winner")
        winner_label = "Player 1" if winner == "PLAYER1" else ("Player 2" if winner == "PLAYER2" else winner)
        steps.append(
            {
                "phase": "end",
                "turn": turn,
                "mover": None,
                "headline": f"GAME OVER — {winner_label} wins ({prettify_token(result.get('reason', ''))})",
                "effects": [],
                "p1": {"coins": p1["coins"], "power": p1["power"], "prestige": p1["prestige"], "agents": p1["agents"]},
                "p2": {"coins": p2["coins"], "power": p2["power"], "prestige": p2["prestige"], "agents": p2["agents"]},
                "patrons": patrons,
                "tavern": [],
            }
        )
    elif steps:
        last = dict(steps[-1])
        last = {**last, "phase": "end", "headline": f"GAME OVER — {result.get('winner', '?')} wins ({prettify_token(result.get('reason', ''))})", "effects": []}
        steps.append(last)

    return {
        "mode": "trace",
        "meta": {
            "bot1": raw.get("bot1"),
            "bot2": raw.get("bot2"),
            "seed": raw.get("seed"),
            "winner": result.get("winner"),
            "reason": result.get("reason"),
        },
        "steps": steps,
    }


# --------------------------------------------------------------------------
# HTML rendering
# --------------------------------------------------------------------------

HTML_TEMPLATE = r"""<!doctype html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8">
<title>Tales of Tribute — Replay: __TITLE__</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
:root {
  --bg: #0f1115;
  --panel: #171a21;
  --panel2: #1d212b;
  --border: #2a2f3a;
  --text: #e6e8ee;
  --muted: #8a90a2;
  --p1: #4fc3f7;
  --p1-dim: #1c3a47;
  --p2: #ffb74d;
  --p2-dim: #4a3620;
  --accent: #7ee787;
  --danger: #ff6b6b;
  --coin: #ffd54f;
  --power: #ef5350;
  --prestige: #ba68c8;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font: 14px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
}
.wrap { max-width: 1180px; margin: 0 auto; padding: 20px 20px 60px; }
header.top { display: flex; justify-content: space-between; align-items: baseline; flex-wrap: wrap; gap: 8px; margin-bottom: 14px; }
header.top h1 { font-size: 18px; margin: 0; font-weight: 600; }
header.top .meta { color: var(--muted); font-size: 12px; }
.legend { display: flex; gap: 16px; align-items: center; font-size: 12px; color: var(--muted); }
.swatch { display: inline-block; width: 10px; height: 10px; border-radius: 3px; margin-right: 5px; vertical-align: -1px; }

.chart-panel { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px; margin-bottom: 14px; }
.chart-panel h2 { font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); margin: 0 0 6px; font-weight: 600; }
svg#chart { width: 100%; height: 130px; display: block; cursor: pointer; }

.controls { display: flex; align-items: center; gap: 10px; background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px; margin-bottom: 14px; }
.controls button {
  background: var(--panel2); color: var(--text); border: 1px solid var(--border);
  border-radius: 6px; padding: 6px 12px; cursor: pointer; font-size: 13px;
}
.controls button:hover { border-color: var(--accent); }
.controls button:disabled { opacity: .35; cursor: default; }
.controls button:disabled:hover { border-color: var(--border); }
#playBtn { min-width: 68px; }
#scrubber { flex: 1; accent-color: var(--accent); }
#stepLabel { font-variant-numeric: tabular-nums; color: var(--muted); font-size: 12px; min-width: 90px; text-align: right; }

.step-card { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 16px 18px; margin-bottom: 14px; }
.step-card .turn-tag { font-size: 11px; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); }
.step-card .headline { font-size: 17px; font-weight: 600; margin: 4px 0 10px; }
.step-card .headline .mover-p1 { color: var(--p1); }
.step-card .headline .mover-p2 { color: var(--p2); }
.effects { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 3px; }
.effects li { font-size: 12.5px; color: var(--muted); padding-left: 14px; position: relative; }
.effects li::before { content: "›"; position: absolute; left: 0; color: var(--accent); }
.effects.empty { display: none; }

.board { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-bottom: 14px; }
@media (max-width: 760px) { .board { grid-template-columns: 1fr; } }
.player-panel { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 14px 16px; border-top: 3px solid var(--p-color); }
.player-panel.p1 { --p-color: var(--p1); }
.player-panel.p2 { --p-color: var(--p2); }
.player-panel h3 { margin: 0 0 10px; font-size: 13px; color: var(--p-color); display: flex; justify-content: space-between; align-items: center; }
.player-panel h3 .bot-name { color: var(--muted); font-weight: 400; font-size: 11px; }
.stat-row { display: flex; gap: 10px; margin-bottom: 10px; }
.stat { flex: 1; background: var(--panel2); border-radius: 8px; padding: 8px 10px; text-align: center; }
.stat .val { font-size: 20px; font-weight: 700; font-variant-numeric: tabular-nums; }
.stat .lbl { font-size: 10px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
.stat.coins .val { color: var(--coin); }
.stat.power .val { color: var(--power); }
.stat.prestige .val { color: var(--prestige); }

.agents { display: flex; flex-wrap: wrap; gap: 6px; min-height: 24px; }
.agent-chip {
  border: 1px solid var(--border); border-radius: 7px; padding: 4px 8px; font-size: 11.5px;
  background: var(--panel2); display: flex; align-items: center; gap: 5px;
}
.agent-chip .hp { color: var(--power); font-weight: 700; }
.agent-chip .taunt { color: var(--accent); font-weight: 700; font-size: 10px; border: 1px solid var(--accent); border-radius: 3px; padding: 0 3px; }
.agent-chip.dormant { opacity: .55; }
.no-agents { color: var(--muted); font-size: 11.5px; font-style: italic; }

.patrons-panel, .tavern-panel { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 14px 16px; margin-bottom: 14px; }
.patrons-panel h2, .tavern-panel h2 { font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); margin: 0 0 10px; font-weight: 600; }
.patron-chips, .tavern-chips { display: flex; flex-wrap: wrap; gap: 8px; }
.patron-chip {
  border-radius: 999px; padding: 5px 12px; font-size: 12px; border: 1px solid var(--border);
  background: var(--panel2);
}
.patron-chip.fav-p1 { border-color: var(--p1); background: var(--p1-dim); color: var(--p1); }
.patron-chip.fav-p2 { border-color: var(--p2); background: var(--p2-dim); color: var(--p2); }
.card-chip {
  border-radius: 7px; padding: 5px 10px; font-size: 12px; border: 1px solid var(--border);
  background: var(--panel2); border-left: 3px solid var(--deck-color, var(--border));
}
.card-chip .cost { color: var(--coin); font-weight: 700; margin-left: 4px; }

footer.notes { color: var(--muted); font-size: 11.5px; margin-top: 24px; border-top: 1px solid var(--border); padding-top: 12px; white-space: pre-wrap; }

.fallback-banner { background: #402020; border: 1px solid var(--danger); color: #ffb4b4; border-radius: 8px; padding: 12px 14px; margin-bottom: 14px; font-size: 13px; }
.msg-line { padding: 6px 0; border-bottom: 1px solid var(--border); font-size: 13px; }
.msg-line .who { font-weight: 600; margin-right: 8px; }
.msg-line.p1 .who { color: var(--p1); }
.msg-line.p2 .who { color: var(--p2); }
</style>
</head>
<body>
<div class="wrap" id="app"></div>
<script id="replay-data" type="application/json">__DATA_JSON__</script>
<script>
const DATA = JSON.parse(document.getElementById('replay-data').textContent);
const app = document.getElementById('app');

function el(tag, attrs, children) {
  const e = document.createElement(tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) {
    if (k === 'class') e.className = v;
    else if (k === 'html') e.innerHTML = v;
    else e.setAttribute(k, v);
  }
  (children || []).forEach(c => { if (c) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
  return e;
}

function deckHue(deck) {
  if (!deck) return 210;
  let h = 0;
  for (let i = 0; i < deck.length; i++) h = (h * 31 + deck.charCodeAt(i)) % 360;
  return h;
}

// ---------------------------------------------------------------------
// Fallback: raw-log-only mode (no reconstructable board state)
// ---------------------------------------------------------------------
if (DATA.mode === 'raw-log-fallback') {
  const banner = el('div', { class: 'fallback-banner' }, [
    'No usable *_trace.json was found for this game, so this is a best-effort fallback: ' +
    'the raw GameRunner text log(s) were parsed for whatever `Log()` messages the bot(s) ' +
    'chose to emit. Per-turn board state (Coins/Power/Prestige/agents/patron favor) is NOT ' +
    'reconstructable from text logs alone and is not shown, to avoid fabricating data.'
  ]);
  app.appendChild(banner);
  app.appendChild(el('h1', {}, [`${DATA.meta.bot1 || '?'} vs ${DATA.meta.bot2 || '?'} — seed ${DATA.meta.seed || '?'}`]));
  if (DATA.messages.length === 0) {
    app.appendChild(el('p', {}, ['No log messages found either — the log file(s) were empty. See the comment header of replay.py for why that happens.']));
  } else {
    const list = el('div', {}, []);
    DATA.messages.forEach(m => {
      const cls = m.player === 'PLAYER1' ? 'p1' : (m.player === 'PLAYER2' ? 'p2' : '');
      const who = m.player ? (m.player === 'PLAYER1' ? 'P1' : 'P2') : '?';
      const loc = (m.turn !== null) ? ` [turn ${m.turn}, move ${m.move}]` : '';
      list.appendChild(el('div', { class: `msg-line ${cls}` }, [
        el('span', { class: 'who' }, [who]),
        el('span', {}, [m.msg + loc]),
      ]));
    });
    app.appendChild(list);
  }
} else {
  // ---------------------------------------------------------------------
  // Full trace mode
  // ---------------------------------------------------------------------
  const steps = DATA.steps;
  let idx = 0;
  let playing = false;
  let timer = null;

  const header = el('header', { class: 'top' }, [
    el('h1', {}, [`${DATA.meta.bot1 || 'Bot 1'} vs ${DATA.meta.bot2 || 'Bot 2'}`]),
    el('div', { class: 'legend' }, [
      el('span', {}, [el('span', { class: 'swatch', html: '' }), '']),
    ]),
  ]);
  header.querySelector('.legend').innerHTML =
    `<span><span class="swatch" style="background:var(--p1)"></span>P1 — ${DATA.meta.bot1 || '?'}</span>` +
    `<span><span class="swatch" style="background:var(--p2)"></span>P2 — ${DATA.meta.bot2 || '?'}</span>` +
    `<span>seed ${DATA.meta.seed ?? '?'}</span>`;
  app.appendChild(header);

  // --- prestige chart ---
  const chartPanel = el('div', { class: 'chart-panel' }, [el('h2', {}, ['Prestige over time'])]);
  const svgNS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNS, 'svg');
  svg.setAttribute('id', 'chart');
  svg.setAttribute('viewBox', '0 0 1000 130');
  svg.setAttribute('preserveAspectRatio', 'none');
  chartPanel.appendChild(svg);
  app.appendChild(chartPanel);

  const W = 1000, H = 130, PAD = 6;
  const maxPrestige = Math.max(40, ...steps.map(s => Math.max(s.p1.prestige, s.p2.prestige)), 10);
  const yFor = (v) => H - PAD - (v / maxPrestige) * (H - PAD * 2);
  const xFor = (i) => steps.length <= 1 ? PAD : PAD + (i / (steps.length - 1)) * (W - PAD * 2);

  function pathFor(key) {
    return steps.map((s, i) => `${i === 0 ? 'M' : 'L'} ${xFor(i).toFixed(1)} ${yFor(s[key].prestige).toFixed(1)}`).join(' ');
  }

  function line(y, dashed) {
    const l = document.createElementNS(svgNS, 'line');
    l.setAttribute('x1', PAD); l.setAttribute('x2', W - PAD);
    l.setAttribute('y1', y); l.setAttribute('y2', y);
    l.setAttribute('stroke', '#3a4050'); l.setAttribute('stroke-width', '1');
    if (dashed) l.setAttribute('stroke-dasharray', '4 3');
    return l;
  }
  svg.appendChild(line(yFor(40), true));
  if (maxPrestige > 45) svg.appendChild(line(yFor(80), true));

  const p1path = document.createElementNS(svgNS, 'path');
  p1path.setAttribute('d', pathFor('p1'));
  p1path.setAttribute('fill', 'none');
  p1path.setAttribute('stroke', 'var(--p1)');
  p1path.setAttribute('stroke-width', '2');
  svg.appendChild(p1path);

  const p2path = document.createElementNS(svgNS, 'path');
  p2path.setAttribute('d', pathFor('p2'));
  p2path.setAttribute('fill', 'none');
  p2path.setAttribute('stroke', 'var(--p2)');
  p2path.setAttribute('stroke-width', '2');
  svg.appendChild(p2path);

  const marker = document.createElementNS(svgNS, 'line');
  marker.setAttribute('y1', 0); marker.setAttribute('y2', H);
  marker.setAttribute('stroke', 'var(--text)'); marker.setAttribute('stroke-width', '1'); marker.setAttribute('stroke-opacity', '0.5');
  svg.appendChild(marker);

  svg.addEventListener('click', (ev) => {
    const rect = svg.getBoundingClientRect();
    const relX = (ev.clientX - rect.left) / rect.width;
    const targetI = Math.round(relX * (steps.length - 1));
    goTo(Math.max(0, Math.min(steps.length - 1, targetI)));
  });

  // --- controls ---
  const controls = el('div', { class: 'controls' }, []);
  const btnFirst = el('button', {}, ['|«']);
  const btnPrev = el('button', {}, ['‹ Prev']);
  const btnPlay = el('button', { id: 'playBtn' }, ['▶ Play']);
  const btnNext = el('button', {}, ['Next ›']);
  const btnLast = el('button', {}, ['»|']);
  const scrubber = el('input', { id: 'scrubber', type: 'range', min: '0', max: String(steps.length - 1), value: '0' });
  const stepLabel = el('span', { id: 'stepLabel' }, ['']);
  [btnFirst, btnPrev, btnPlay, btnNext, btnLast].forEach(b => controls.appendChild(b));
  controls.appendChild(scrubber);
  controls.appendChild(stepLabel);
  app.appendChild(controls);

  // --- step card ---
  const stepCard = el('div', { class: 'step-card' }, []);
  app.appendChild(stepCard);

  // --- board ---
  const board = el('div', { class: 'board' }, []);
  const p1Panel = el('div', { class: 'player-panel p1' }, []);
  const p2Panel = el('div', { class: 'player-panel p2' }, []);
  board.appendChild(p1Panel);
  board.appendChild(p2Panel);
  app.appendChild(board);

  const patronsPanel = el('div', { class: 'patrons-panel' }, [el('h2', {}, ['Patron favor'])]);
  const patronChips = el('div', { class: 'patron-chips' }, []);
  patronsPanel.appendChild(patronChips);
  app.appendChild(patronsPanel);

  const tavernPanel = el('div', { class: 'tavern-panel' }, [el('h2', {}, ['Tavern (shared, 5 cards)'])]);
  const tavernChips = el('div', { class: 'tavern-chips' }, []);
  tavernPanel.appendChild(tavernChips);
  app.appendChild(tavernPanel);

  app.appendChild(el('footer', { class: 'notes' }, [
    'Playback: ← / → to step, Space to play/pause, click the chart to jump. ' +
    'Hidden opponent hand contents are never shown (only what is public in the real game: ' +
    'resources, agents in play, and patron favor). See the top of scripts/replay.py for full notes on how this trace was produced.'
  ]));

  function playerPanel(panel, side, cls, botName) {
    panel.innerHTML = '';
    panel.appendChild(el('h3', {}, [`Player ${side === 'p1' ? '1' : '2'}`, el('span', { class: 'bot-name' }, [botName || ''])]));
    const statsRow = el('div', { class: 'stat-row' }, []);
    ['coins', 'power', 'prestige'].forEach(k => {
      statsRow.appendChild(el('div', { class: `stat ${k}` }, [
        el('div', { class: 'val', id: `${side}-${k}` }, ['0']),
        el('div', { class: 'lbl' }, [k]),
      ]));
    });
    panel.appendChild(statsRow);
    panel.appendChild(el('div', { class: 'agents', id: `${side}-agents` }, []));
  }
  playerPanel(p1Panel, 'p1', 'p1', DATA.meta.bot1);
  playerPanel(p2Panel, 'p2', 'p2', DATA.meta.bot2);

  function renderAgents(container, agents) {
    container.innerHTML = '';
    if (!agents.length) {
      container.appendChild(el('div', { class: 'no-agents' }, ['(no agents in play)']));
      return;
    }
    agents.forEach(a => {
      const chip = el('div', { class: `agent-chip${a.activated ? '' : ' dormant'}` }, []);
      chip.style.borderLeft = `3px solid hsl(${deckHue(a.deck)},55%,50%)`;
      chip.appendChild(el('span', {}, [a.name]));
      chip.appendChild(el('span', { class: 'hp' }, [`${a.hp}${a.maxHp && a.maxHp > 0 ? '/' + a.maxHp : ''} HP`]));
      if (a.taunt) chip.appendChild(el('span', { class: 'taunt' }, ['TAUNT']));
      container.appendChild(chip);
    });
  }

  function render() {
    const s = steps[idx];
    scrubber.value = String(idx);
    stepLabel.textContent = `step ${idx + 1} / ${steps.length}`;
    marker.setAttribute('x1', xFor(idx)); marker.setAttribute('x2', xFor(idx));

    stepCard.innerHTML = '';
    const tag = s.phase === 'setup' ? 'Patron Draft' : (s.phase === 'end' ? 'Game Over' : `Turn ${s.turn}`);
    stepCard.appendChild(el('div', { class: 'turn-tag' }, [tag]));
    const moverCls = s.mover === 'P1' ? 'mover-p1' : (s.mover === 'P2' ? 'mover-p2' : '');
    const moverLabel = s.mover ? (s.mover === 'P1' ? 'Player 1 ' : 'Player 2 ') : '';
    const headlineEl = el('div', { class: 'headline' }, []);
    if (s.mover) headlineEl.appendChild(el('span', { class: moverCls }, [moverLabel]));
    headlineEl.appendChild(document.createTextNode(s.headline));
    stepCard.appendChild(headlineEl);
    const effList = el('ul', { class: `effects${s.effects.length ? '' : ' empty'}` }, []);
    s.effects.forEach(e => effList.appendChild(el('li', {}, [e])));
    stepCard.appendChild(effList);

    ['p1', 'p2'].forEach(side => {
      document.getElementById(`${side}-coins`).textContent = s[side].coins;
      document.getElementById(`${side}-power`).textContent = s[side].power;
      document.getElementById(`${side}-prestige`).textContent = s[side].prestige;
      renderAgents(document.getElementById(`${side}-agents`), s[side].agents);
    });

    patronChips.innerHTML = '';
    Object.entries(s.patrons).forEach(([name, fav]) => {
      const cls = fav === 'P1' ? 'fav-p1' : (fav === 'P2' ? 'fav-p2' : '');
      patronChips.appendChild(el('div', { class: `patron-chip ${cls}` }, [name.replace(/_/g, ' ')]));
    });

    tavernChips.innerHTML = '';
    if (!s.tavern.length) {
      tavernChips.appendChild(el('div', { class: 'no-agents' }, ['(not available at this step)']));
    } else {
      s.tavern.forEach(c => {
        const chip = el('div', { class: 'card-chip' }, [c.name, el('span', { class: 'cost' }, [String(c.cost)])]);
        chip.style.setProperty('--deck-color', `hsl(${deckHue(c.deck)},55%,50%)`);
        tavernChips.appendChild(chip);
      });
    }

    btnFirst.disabled = btnPrev.disabled = idx === 0;
    btnLast.disabled = btnNext.disabled = idx === steps.length - 1;
  }

  function goTo(i) { idx = Math.max(0, Math.min(steps.length - 1, i)); render(); }
  function next() { if (idx < steps.length - 1) { goTo(idx + 1); } else { stop(); } }
  function prev() { goTo(idx - 1); }

  function play() {
    playing = true;
    btnPlay.textContent = '⏸ Pause';
    timer = setInterval(() => { if (idx >= steps.length - 1) stop(); else next(); }, 900);
  }
  function stop() {
    playing = false;
    btnPlay.textContent = '▶ Play';
    if (timer) clearInterval(timer);
    timer = null;
  }
  function togglePlay() { playing ? stop() : play(); }

  btnFirst.addEventListener('click', () => { stop(); goTo(0); });
  btnPrev.addEventListener('click', () => { stop(); prev(); });
  btnNext.addEventListener('click', () => { stop(); next(); });
  btnLast.addEventListener('click', () => { stop(); goTo(steps.length - 1); });
  btnPlay.addEventListener('click', togglePlay);
  scrubber.addEventListener('input', () => { stop(); goTo(parseInt(scrubber.value, 10)); });

  window.addEventListener('keydown', (ev) => {
    if (ev.key === 'ArrowRight') { stop(); next(); ev.preventDefault(); }
    else if (ev.key === 'ArrowLeft') { stop(); prev(); ev.preventDefault(); }
    else if (ev.key === ' ') { togglePlay(); ev.preventDefault(); }
  });

  render();
}
</script>
</body>
</html>
"""


def render_html(data: dict, title: str) -> str:
    payload = json.dumps(data, ensure_ascii=False)
    # Guard against a literal "</script>" appearing inside the JSON payload.
    payload = payload.replace("</script>", "<\\/script>")
    out = HTML_TEMPLATE.replace("__DATA_JSON__", payload)
    out = out.replace("__TITLE__", html.escape(title))
    return out


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("inputs", nargs="+", help="log directory, or explicit .json/.log file path(s)")
    ap.add_argument("-o", "--output", default="results/replay.html", help="output HTML path")
    ap.add_argument("--seed", default=None, help="seed label to use in raw-log fallback mode")
    ap.add_argument("--bot1", default=None, help="bot1 name to use in raw-log fallback mode")
    ap.add_argument("--bot2", default=None, help="bot2 name to use in raw-log fallback mode")
    args = ap.parse_args()

    trace_path, log_paths = find_inputs(args.inputs)

    if trace_path is not None:
        raw = json.loads(trace_path.read_text())
        data = compact_trace(raw)
        title = f"{data['meta']['bot1']} vs {data['meta']['bot2']} (seed {data['meta']['seed']})"
    elif log_paths:
        # best guess at seed/bot names from the filename, overridable via flags
        seed_guess = args.seed
        if seed_guess is None and log_paths:
            m = re.match(r"(\d+)_p[12]\.log$", log_paths[0].name)
            seed_guess = m.group(1) if m else "?"
        data = parse_raw_logs(log_paths, args.bot1 or "Bot1", args.bot2 or "Bot2", seed_guess or "?")
        title = f"{data['meta']['bot1']} vs {data['meta']['bot2']} (seed {data['meta']['seed']}) [raw-log fallback]"
    else:
        print("error: no *_trace.json or *.log files found in the given input(s)", file=sys.stderr)
        return 1

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(render_html(data, title))
    print(f"wrote {out_path} ({out_path.stat().st_size / 1024:.1f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
