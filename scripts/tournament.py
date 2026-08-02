#!/usr/bin/env python3
"""Resumable shadow round-robin tournament harness for Scripts of Tribute bots.

Builds on scripts/bench.py: same GameRunner invocation, same mirror-pair
seeding convention (same seed, seats swapped), same stdout parsing and Wilson
CI. This script adds an all-play-all round robin across a bot list, chunked
+ checkpointed to results/tournament-<tag>.jsonl so a kill -9 mid-run never
loses more than one in-flight chunk.

Usage:
  # play it out (frozen snapshot runner, small smoke test)
  RIPTIDE_RUNNER=$PWD/snapshots/v1/GameRunner.dll \\
    python3 scripts/tournament.py --tag smoke --pairs 1 --workers 4 \\
    --bots Riptide,BestMCTS3,EBot_C

  # full field, default bot list (Riptide + the 6 compiled-in 2025 bots)
  python3 scripts/tournament.py --tag full --pairs 25 --workers 8

  # resume: same command, same --tag -- already-recorded chunks are skipped
  python3 scripts/tournament.py --tag full --pairs 25 --workers 8

  # regenerate standings + h2h matrix from the jsonl without playing games
  python3 scripts/tournament.py --tag full --report-only
"""
import argparse
import json
import math
import os
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bench  # noqa: E402  (reuses run_games / wilson_ci / RUNNER resolution)

RESULT_DIR = ROOT / "results"

DEFAULT_BOTS = ["Riptide"] + bench.FIELD_2025  # Riptide + the 6 2025 field bots

# 2025 official field win rates (from ranking.csv / docs/benchmarks.md),
# printed alongside our measured numbers for comparison. Riptide and any
# other bot not in the 2025 field simply has no reference entry.
FIELD_2025_REFERENCE_PCT = {
    "SakkirinaSolo": 71.83823529411765,
    "Sakkirina": 70.60661764705883,
    "HQL_Plus_BOT": 69.20955882352942,
    "BestMCTS3": 66.06617647058823,
    "EBot_C": 56.029411764705884,
    "EBot_F": 51.783088235294116,
}

_write_lock = threading.Lock()


# ---------------------------------------------------------------------------
# Job construction
# ---------------------------------------------------------------------------

def build_jobs(bots: list[str], pairs: int, seed: int, chunk_size: int) -> list[dict]:
    """All-play-all round robin: for every unordered pair, split `pairs` mirror
    pairs into fixed-size chunks (independent of --workers, so resume is not
    invalidated by changing --workers between runs). Each chunk becomes two
    jobs (each side as P1) on the same seed range, matching bench.py's
    mirror_match convention.
    """
    jobs = []
    for i in range(len(bots)):
        for j in range(i + 1, len(bots)):
            a, b = bots[i], bots[j]
            s = seed
            left = pairs
            while left > 0:
                n = min(chunk_size, left)
                jobs.append({"pair": [a, b], "bot1": a, "bot2": b, "n": n, "seed": s, "a_is_p1": True})
                jobs.append({"pair": [a, b], "bot1": b, "bot2": a, "n": n, "seed": s, "a_is_p1": False})
                s += n
                left -= n
    return jobs


def job_key(job: dict) -> tuple:
    return (job["bot1"], job["bot2"], job["n"], job["seed"])


def load_completed(path: Path) -> tuple[set, list[dict]]:
    """Return (set of completed job keys, list of all valid records) from the
    jsonl. Tolerant of a truncated last line (mid-write kill)."""
    completed = set()
    records = []
    if not path.exists():
        return completed, records
    with open(path, "r") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                # Torn write from a kill mid-append; the record never made it
                # to disk intact, so just drop this line and keep going.
                continue
            completed.add((rec["bot1"], rec["bot2"], rec["n"], rec["seed"]))
            records.append(rec)
    return completed, records


def append_record(path: Path, rec: dict):
    """Append one completed chunk, flushed + fsynced so a kill right after
    this call cannot lose it."""
    line = json.dumps(rec) + "\n"
    with _write_lock:
        with open(path, "a") as f:
            f.write(line)
            f.flush()
            os.fsync(f.fileno())


# ---------------------------------------------------------------------------
# Running
# ---------------------------------------------------------------------------

def run_job(job: dict, timeout: int) -> dict:
    p1w, p2w, dr, ot = bench.run_games(job["bot1"], job["bot2"], job["n"], job["seed"], timeout)
    return {
        "pair": job["pair"], "bot1": job["bot1"], "bot2": job["bot2"],
        "n": job["n"], "seed": job["seed"], "a_is_p1": job["a_is_p1"],
        "p1w": p1w, "p2w": p2w, "draws": dr, "other": ot,
        "ts": time.time(),
    }


def play_tournament(bots, pairs, seed, timeout, workers, chunk_size, jsonl_path):
    all_jobs = build_jobs(bots, pairs, seed, chunk_size)
    completed, existing_records = load_completed(jsonl_path)
    pending = [j for j in all_jobs if job_key(j) not in completed]

    n_matchups = len(bots) * (len(bots) - 1) // 2
    print(f"Round robin: {len(bots)} bots, {n_matchups} matchups, {pairs} mirror pairs each.")
    print(f"Jobs: {len(all_jobs)} total, {len(all_jobs) - len(pending)} already recorded "
          f"(resumed from {jsonl_path.name}), {len(pending)} to run.")
    if not pending:
        print("Nothing to do -- all chunks already recorded.")
        return existing_records

    records = list(existing_records)
    done = 0
    with ThreadPoolExecutor(max_workers=workers) as ex:
        futs = {ex.submit(run_job, j, timeout): j for j in pending}
        for fut in as_completed(futs):
            job = futs[fut]
            rec = fut.result()
            append_record(jsonl_path, rec)
            records.append(rec)
            done += 1
            flag = "  <<< OTHER-FACTORS" if rec["other"] else ""
            print(f"  [{done}/{len(pending)}] {rec['bot1']} vs {rec['bot2']} "
                  f"n={rec['n']} seed={rec['seed']}: "
                  f"P1 {rec['p1w']}/{rec['n']} P2 {rec['p2w']}/{rec['n']} "
                  f"draws={rec['draws']} other={rec['other']}{flag}", flush=True)
    return records


# ---------------------------------------------------------------------------
# Aggregation / reporting
# ---------------------------------------------------------------------------

def aggregate(records, bots):
    """Per-bot totals + per-pair head-to-head totals from raw chunk records."""
    bot_set = set(bots)
    totals = {b: {"wins": 0, "losses": 0, "draws": 0, "games": 0, "other": 0} for b in bots}
    h2h = {}  # (a, b) -> {"a_wins":, "games":} for a as row bot vs b as column bot

    def h2h_bucket(a, b):
        return h2h.setdefault((a, b), {"wins": 0, "games": 0})

    for rec in records:
        bot1, bot2 = rec["bot1"], rec["bot2"]
        if bot1 not in bot_set or bot2 not in bot_set:
            continue  # record from a different --bots configuration under the same tag
        n, p1w, p2w, dr, ot = rec["n"], rec["p1w"], rec["p2w"], rec["draws"], rec["other"]
        games = p1w + p2w + dr

        for b in (bot1, bot2):
            totals[b]["other"] += ot

        totals[bot1]["wins"] += p1w
        totals[bot1]["losses"] += p2w
        totals[bot1]["draws"] += dr
        totals[bot1]["games"] += games

        totals[bot2]["wins"] += p2w
        totals[bot2]["losses"] += p1w
        totals[bot2]["draws"] += dr
        totals[bot2]["games"] += games

        b1 = h2h_bucket(bot1, bot2)
        b1["wins"] += p1w
        b1["games"] += games
        b2 = h2h_bucket(bot2, bot1)
        b2["wins"] += p2w
        b2["games"] += games

    return totals, h2h


def fmt_pct(x):
    return f"{100 * x:.1f}%"


def print_standings(totals, bots):
    rows = []
    for b in bots:
        t = totals[b]
        g = t["games"]
        wr = t["wins"] / g if g else 0.0
        lo, hi = bench.wilson_ci(t["wins"], g) if g else (0.0, 0.0)
        rows.append((b, t, wr, lo, hi))
    rows.sort(key=lambda r: r[2], reverse=True)

    any_other = any(t["other"] for _, t, *_ in rows)

    header = f"{'Rank':<5}{'Bot':<16}{'W':>6}{'L':>6}{'D':>4}{'Games':>7}{'Win%':>8}{'95% CI':>16}{'2025 Ref%':>11}{'Other':>7}"
    print(header)
    print("-" * len(header))
    for rank, (b, t, wr, lo, hi) in enumerate(rows, 1):
        ref = FIELD_2025_REFERENCE_PCT.get(b)
        ref_s = f"{ref:.1f}%" if ref is not None else "n/a"
        ci_s = f"{100*lo:.1f}-{100*hi:.1f}%"
        other_s = str(t["other"])
        flag = " <<<" if t["other"] else ""
        print(f"{rank:<5}{b:<16}{t['wins']:>6}{t['losses']:>6}{t['draws']:>4}{t['games']:>7}"
              f"{fmt_pct(wr):>8}{ci_s:>16}{ref_s:>11}{other_s + flag:>7}")

    if any_other:
        print("\n!!! NONZERO OTHER-FACTORS COUNT (TURN_TIMEOUT/BOT_EXCEPTION/INCORRECT_MOVE) "
              "-- affected matchups' batches are suspect. See per-matchup detail below. !!!")
    else:
        print("\nOther-factors count: 0 across all matchups (clean run).")


def print_h2h_matrix(h2h, bots):
    print(f"\nHead-to-head win rate (row bot's win rate vs column bot):\n")
    col_w = max(10, max(len(b) for b in bots) + 1)
    print(" " * 17 + "".join(f"{b:>{col_w}}" for b in bots))
    for a in bots:
        cells = []
        for b in bots:
            if a == b:
                cells.append(f"{'--':>{col_w}}")
                continue
            bucket = h2h.get((a, b))
            if not bucket or bucket["games"] == 0:
                cells.append(f"{'n/a':>{col_w}}")
            else:
                wr = bucket["wins"] / bucket["games"]
                cells.append(f"{fmt_pct(wr):>{col_w}}")
        print(f"{a:<17}" + "".join(cells))


def print_matchup_other_factors(records, bots):
    bot_set = set(bots)
    per_pair_other = {}
    for rec in records:
        if rec["bot1"] not in bot_set or rec["bot2"] not in bot_set:
            continue
        if rec["other"] == 0:
            continue
        key = tuple(sorted((rec["bot1"], rec["bot2"])))
        per_pair_other[key] = per_pair_other.get(key, 0) + rec["other"]
    if not per_pair_other:
        return
    print("\nMatchups with nonzero other-factors endings:")
    for (a, b), cnt in sorted(per_pair_other.items(), key=lambda kv: -kv[1]):
        print(f"  {a} vs {b}: {cnt}   <<< BATCH SUSPECT")


def report(records, bots, tag):
    totals, h2h = aggregate(records, bots)
    n_games = sum(t["games"] for t in totals.values()) // 2
    print(f"\n==================== TOURNAMENT STANDINGS (tag={tag}) ====================")
    print(f"{len(bots)} bots, {n_games} total games recorded\n")
    print_standings(totals, bots)
    print_h2h_matrix(h2h, bots)
    print_matchup_other_factors(records, bots)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bots", type=str, default=",".join(DEFAULT_BOTS),
                     help="comma-separated bot list (default: Riptide + 2025 field)")
    ap.add_argument("--pairs", type=int, default=25, help="mirror pairs per matchup")
    ap.add_argument("--seed", type=int, default=1000, help="base seed (reused per matchup, like bench.py --field)")
    ap.add_argument("--timeout", type=int, default=10, help="seconds per turn (competition: 10)")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--chunk-size", type=int, default=5,
                     help="mirror pairs per checkpointed chunk (independent of --workers, "
                          "so resume survives changing --workers between runs)")
    ap.add_argument("--tag", type=str, required=True,
                     help="results/tournament-<tag>.jsonl -- reuse the same tag to resume")
    ap.add_argument("--report-only", action="store_true",
                     help="regenerate standings + h2h matrix from the jsonl without playing games")
    args = ap.parse_args()

    bots = [b.strip() for b in args.bots.split(",") if b.strip()]
    if len(bots) < 2:
        sys.exit("Need at least 2 bots.")
    if len(set(bots)) != len(bots):
        sys.exit(f"Duplicate bot names in --bots: {bots}")

    RESULT_DIR.mkdir(exist_ok=True)
    jsonl_path = RESULT_DIR / f"tournament-{args.tag}.jsonl"

    if args.report_only:
        _, records = load_completed(jsonl_path)
        if not records:
            sys.exit(f"No records found in {jsonl_path} -- nothing to report.")
        report(records, bots, args.tag)
        return

    print(f"Runner: {bench.RUNNER}")
    records = play_tournament(bots, args.pairs, args.seed, args.timeout, args.workers,
                               args.chunk_size, jsonl_path)
    report(records, bots, args.tag)


if __name__ == "__main__":
    main()
