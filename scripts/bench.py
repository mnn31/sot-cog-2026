#!/usr/bin/env python3
"""Mirror-match benchmark harness for Scripts of Tribute bots.

Replicates the competition evaluation: pairs of games on the same seed with
seats swapped, 10 s/turn, aggregated win rate with a binomial CI.

Usage:
  python3 scripts/bench.py BotA BotB --pairs 50 --seed 1000 --workers 4
  python3 scripts/bench.py MyBot --field --pairs 25   # round-robin vs 2025 top bots
"""
import argparse
import math
import os
import re
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNNER = ROOT / "ScriptsOfTribute-Core/GameRunner/bin/Release/net8.0/GameRunner.dll"
FIELD_2025 = ["SakkirinaSolo", "Sakkirina", "HQL_Plus_BOT", "BestMCTS3", "EBot_C", "EBot_F"]

P1_RE = re.compile(r"P1 wins: (\d+)/(\d+)")
P2_RE = re.compile(r"P2 wins: (\d+)/(\d+)")
DRAW_RE = re.compile(r"draws: (\d+)/(\d+)")


def run_games(bot1: str, bot2: str, n: int, seed: int, turn_timeout: int) -> tuple[int, int, int]:
    """Run n games of bot1 (P1) vs bot2 (P2) on seeds seed..seed+n-1.

    Returns (p1_wins, p2_wins, draws)."""
    cmd = [
        "dotnet", str(RUNNER), bot1, bot2,
        "-n", str(n), "-s", str(seed), "-to", str(turn_timeout),
    ]
    out = subprocess.run(
        cmd, capture_output=True, text=True,
        env={**os.environ, "DOTNET_ROLL_FORWARD": "LatestMajor"},
        cwd=RUNNER.parent,
    ).stdout
    p1 = P1_RE.search(out)
    p2 = P2_RE.search(out)
    dr = DRAW_RE.search(out)
    if not (p1 and p2):
        sys.exit(f"Could not parse GameRunner output for {bot1} vs {bot2}:\n{out[-2000:]}")
    return int(p1.group(1)), int(p2.group(1)), int(dr.group(1)) if dr else 0


def wilson_ci(wins: float, n: int, z: float = 1.96) -> tuple[float, float]:
    if n == 0:
        return (0.0, 1.0)
    p = wins / n
    denom = 1 + z * z / n
    center = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return center - half, center + half


def mirror_match(a: str, b: str, pairs: int, seed: int, timeout: int, workers: int):
    """pairs games as A=P1 plus the same seeds as B=P1, run in seed chunks."""
    chunk = max(1, pairs // workers)
    jobs = []  # (bot1, bot2, n, seed, a_is_p1)
    s = seed
    left = pairs
    while left > 0:
        n = min(chunk, left)
        jobs.append((a, b, n, s, True))
        jobs.append((b, a, n, s, False))
        s += n
        left -= n
    a_wins = b_wins = draws = 0
    a_seat1_wins = a_seat2_wins = 0
    with ThreadPoolExecutor(max_workers=workers) as ex:
        results = list(ex.map(lambda j: (j, run_games(j[0], j[1], j[2], j[3], timeout)), jobs))
    for (bot1, bot2, n, s0, a_is_p1), (p1w, p2w, dr) in results:
        draws += dr
        if a_is_p1:
            a_wins += p1w
            b_wins += p2w
            a_seat1_wins += p1w
        else:
            b_wins += p1w
            a_wins += p2w
            a_seat2_wins += p2w
    total = a_wins + b_wins + draws
    lo, hi = wilson_ci(a_wins, total)
    print(f"\n=== {a} vs {b} — {total} games ({pairs} mirror pairs, seeds {seed}..{seed + pairs - 1}) ===")
    print(f"{a}: {a_wins} wins ({100 * a_wins / total:.1f}%)  [95% CI {100 * lo:.1f}–{100 * hi:.1f}%]")
    print(f"  as P1: {a_seat1_wins}/{pairs}  as P2: {a_seat2_wins}/{pairs}  draws: {draws}")
    return a_wins, b_wins, draws


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bots", nargs="+", help="BotA [BotB] (class name or Namespace.Class)")
    ap.add_argument("--pairs", type=int, default=25, help="mirror pairs per matchup")
    ap.add_argument("--seed", type=int, default=1000)
    ap.add_argument("--timeout", type=int, default=10, help="seconds per turn (competition: 10)")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--field", action="store_true", help="run bots[0] against the 2025 field")
    args = ap.parse_args()

    if args.field:
        me = args.bots[0]
        total_w = total_g = 0
        for opp in FIELD_2025:
            if opp == me:
                continue
            w, l, d = mirror_match(me, opp, args.pairs, args.seed, args.timeout, args.workers)
            total_w += w
            total_g += w + l + d
        lo, hi = wilson_ci(total_w, total_g)
        print(f"\n#### FIELD WINRATE for {me}: {100 * total_w / total_g:.1f}% "
              f"({total_w}/{total_g}) [95% CI {100 * lo:.1f}–{100 * hi:.1f}%]")
        print("2025 reference: SakkirinaSolo 71.8%, Sakkirina 70.6%, HQL+ 69.2%, BestMCTS3 66.1%")
    else:
        if len(args.bots) != 2:
            sys.exit("Provide exactly two bots, or use --field")
        mirror_match(args.bots[0], args.bots[1], args.pairs, args.seed, args.timeout, args.workers)


if __name__ == "__main__":
    main()
