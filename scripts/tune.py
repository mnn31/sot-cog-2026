#!/usr/bin/env python3
"""SPSA tuner for Riptide's evaluation weights.

Every candidate is a flat-JSON weight file consumed by the bot through the
RIPTIDE_WEIGHTS environment variable (see bot/RiptideWeights.cs), so a tuning
iteration costs zero rebuilds.

SPSA (Spall 1992) needs exactly two objective evaluations per iteration no
matter how many parameters there are, which is the only thing that fits when a
single objective evaluation costs ~30 games of Tales of Tribute.

Parameterisation: x_i = log(w_i / default_i), starting at x = 0. Log space keeps
every weight positive (all defaults are positive and a sign flip would be
nonsense for all of them) and makes one perturbation size c meaningful across
weights spanning 0.15 to 2000.

One iteration:
  delta ~ Rademacher(+/-1)^d
  w+ = default * exp(x + c*delta),  w- = default * exp(x - c*delta)
  y+ , y- = win rate vs the fixed opponent over the SAME seed set (common
            random numbers: paired, so the seed-luck component cancels)
  ghat_i  = (y+ - y-) / (2*c*delta_i)
  x      <- clip(x + a*ghat)                      (ascent: we maximise win rate)

Seeds are fresh every iteration, so the trajectory is not fitted to one seed
set; pairing is only needed *within* an iteration.

Usage:
  scripts/tune.py dump --out weights/baseline.json
  scripts/tune.py run  --iters 10 --pairs 16 --workers 10 --budget-min 90
  scripts/tune.py validate --candidates default results/tuned-final.json \\
                           --pairs 16 --seed 9000 --workers 10
"""
import argparse
import json
import math
import os
import random
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNNER = Path(os.environ.get(
    "RIPTIDE_RUNNER",
    ROOT / "ScriptsOfTribute-Core/GameRunner/bin/Release/net8.0/GameRunner.dll"))
WEIGHT_DIR = ROOT / "weights"
RESULT_DIR = ROOT / "results"
LOG_PATH = Path(os.environ.get("RIPTIDE_TUNE_LOG", RESULT_DIR / "tuning-log.jsonl"))

P1_RE = re.compile(r"P1 wins: (\d+)/(\d+)")
P2_RE = re.compile(r"P2 wins: (\d+)/(\d+)")
DRAW_RE = re.compile(r"draws: (\d+)/(\d+)")
OTHER_RE = re.compile(r"Ends due to other factors: (\d+)/(\d+)")

# ---------------------------------------------------------------------------
# The tunable scalar set. Values MUST match the compiled-in defaults in
# bot/RiptideEval.cs — `scripts/tune.py check` verifies that by parsing the C#.
# ---------------------------------------------------------------------------
DEFAULTS = {
    "eval.Scale": 780.0,
    "eval.PrestigeBase": 6.0,
    "eval.PrestigeSlope": 34.0,
    "eval.TempoOffset": 1.0,
    "eval.Lead40Bonus": 900.0,
    "eval.Behind40Penalty": 1400.0,
    "eval.ApproachStep": 22.0,
    "eval.OwnApproachStep": 14.0,
    "eval.Danger80Step": 120.0,
    "eval.Own80Step": 70.0,
    "eval.PatronDiff": 55.0,
    "eval.EnemyPatronDist1": 2000.0,
    "eval.EnemyPatronDist2": 260.0,
    "eval.EnemyPatronDist3": 40.0,
    "eval.OwnPatronDist1": 900.0,
    "eval.OwnPatronDist2": 160.0,
    "eval.OwnPatronDist3": 30.0,
    "eval.CrowsHoldPenalty": 90.0,
    "eval.AnseiFavourBonus": 25.0,
    "eval.DeckWeight": 1.8,
    "eval.DeckNorm": 16.0,
    "eval.ComboWeight": 260.0,
    "eval.BewildermentExtra": 22.0,
    "eval.MyAgentCard": 0.90,
    "eval.MyAgentHp": 7.0,
    "eval.MyAgentTaunt": 12.0,
    "eval.EnemyAgentCard": 1.05,
    "eval.EnemyAgentHp": 8.0,
    "eval.EnemyAgentFlat": 15.0,
    "eval.TavernDeny": 0.40,
    "eval.TavernDenyAffinity": 1.10,
    "eval.CoinValue": 4.0,
    "eval.PatronCallValue": 22.0,
    "eval.UpcomingDrawValue": 0.35,
    "eval.ComboGe2": 0.55,
    "eval.ComboGe3": 0.30,
    "eval.ComboGe4": 0.15,
    "eval.EnemyCoinValue": 0.5,
}
KEYS = list(DEFAULTS)

# SPSA gains. The objective (win rate over 32 games) has a standard error of
# ~0.09 even with common random numbers, so the perturbation is deliberately
# large: a bigger c trades finite-difference bias for signal-to-noise, and in
# this regime we are entirely noise-limited. At c = 0.45 each weight is probed
# at x1.57 and /1.57; a 2-game (of 32) difference then moves a weight ~10%, a
# 4-game difference ~21%.
C_PERTURB = 0.45        # log-space perturbation half-width (~ x1.57 / /1.57)
A_GAIN = 0.75           # ascent gain
STEP_CLIP = 0.35        # max |dx| per parameter per iteration
X_CLIP = 1.20           # max |x| overall (~ x3.3 / /3.3 from the default)


# ---------------------------------------------------------------------------
# weights <-> x
# ---------------------------------------------------------------------------
def x_to_theta(x):
    return {k: DEFAULTS[k] * math.exp(x[i]) for i, k in enumerate(KEYS)}


def write_weights(path: Path, theta: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w") as f:
        json.dump({k: round(v, 6) for k, v in theta.items()}, f, indent=1, sort_keys=True)
    return path


# ---------------------------------------------------------------------------
# game running
# ---------------------------------------------------------------------------
def run_chunk(bot1, bot2, n, seed, timeout, weights_path):
    env = {**os.environ, "DOTNET_ROLL_FORWARD": "LatestMajor"}
    if weights_path is None:
        env.pop("RIPTIDE_WEIGHTS", None)
    else:
        env["RIPTIDE_WEIGHTS"] = str(weights_path)
    cmd = ["dotnet", str(RUNNER), bot1, bot2,
           "-n", str(n), "-s", str(seed), "-to", str(timeout)]
    proc = subprocess.run(cmd, capture_output=True, text=True, env=env, cwd=RUNNER.parent)
    if "WEIGHTS LOAD FAILED" in proc.stderr:
        sys.exit("FATAL: bot could not load %s\n%s" % (weights_path, proc.stderr[:1000]))
    if weights_path is not None and "[riptide] weights" not in proc.stderr:
        sys.exit("FATAL: bot never announced a weight file for %s — env not "
                 "reaching the process?\n%s" % (weights_path, proc.stderr[:1000]))
    p1 = P1_RE.search(proc.stdout)
    p2 = P2_RE.search(proc.stdout)
    dr = DRAW_RE.search(proc.stdout)
    if not (p1 and p2):
        sys.exit("Could not parse GameRunner output:\n%s" % proc.stdout[-2000:])
    ot = OTHER_RE.search(proc.stdout)
    other = int(ot.group(1)) if ot else 0
    if other:
        # TURN_TIMEOUT / BOT_EXCEPTION / INCORRECT_MOVE. Never seen in v1; if it
        # starts happening the measurement is contaminated and must be flagged.
        print("  !! %d game(s) ended on 'other factors' (%s vs %s, seed %d)"
              % (other, bot1, bot2, seed), file=sys.stderr)
    return int(p1.group(1)), int(p2.group(1)), int(dr.group(1)) if dr else 0, other


def build_jobs(label, weights_path, seeds, opponent, timeout, chunk):
    """Mirror pairs: Riptide as P1 and as P2 on every seed."""
    jobs = []
    for i in range(0, len(seeds), chunk):
        block = seeds[i:i + chunk]
        s0, n = block[0], len(block)
        assert block == list(range(s0, s0 + n)), "seeds must be contiguous within a chunk"
        jobs.append((label, weights_path, "Riptide", opponent, n, s0, timeout, True))
        jobs.append((label, weights_path, opponent, "Riptide", n, s0, timeout, False))
    return jobs


def execute(jobs, workers):
    def one(j):
        label, wp, b1, b2, n, s0, to, first = j
        return j, run_chunk(b1, b2, n, s0, to, wp)
    with ThreadPoolExecutor(max_workers=workers) as ex:
        return list(ex.map(one, jobs))


def tally(results):
    agg = {}
    for (label, wp, b1, b2, n, s0, to, riptide_p1), (p1w, p2w, dr, other) in results:
        a = agg.setdefault(label, {"wins": 0, "losses": 0, "draws": 0,
                                   "as_p1": 0, "as_p2": 0, "games": 0, "other": 0})
        a["draws"] += dr
        a["games"] += n
        a["other"] += other
        if riptide_p1:
            a["wins"] += p1w
            a["losses"] += p2w
            a["as_p1"] += p1w
        else:
            a["wins"] += p2w
            a["losses"] += p1w
            a["as_p2"] += p2w
    for a in agg.values():
        a["rate"] = a["wins"] / a["games"] if a["games"] else 0.0
    return agg


def wilson(wins, n, z=1.96):
    if n == 0:
        return 0.0, 1.0
    p = wins / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h


# ---------------------------------------------------------------------------
# SPSA
# ---------------------------------------------------------------------------
def load_state():
    """Resume from the last logged iteration, if any."""
    if not LOG_PATH.exists():
        return 0, [0.0] * len(KEYS), None, []
    recs = []
    with open(LOG_PATH) as f:
        for line in f:
            line = line.strip()
            if line:
                recs.append(json.loads(line))
    iters = [r for r in recs if r.get("kind") == "spsa_iter"]
    if not iters:
        return 0, [0.0] * len(KEYS), None, recs
    last = iters[-1]
    return last["iter"] + 1, list(last["x_next"]), last, recs


def log_rec(rec):
    RESULT_DIR.mkdir(parents=True, exist_ok=True)
    with open(LOG_PATH, "a") as f:
        f.write(json.dumps(rec) + "\n")
        f.flush()


def cmd_run(args):
    random.seed(args.rng_seed)
    it0, x, _, recs = load_state()
    # Replay the RNG so a resumed run does not repeat perturbation directions.
    for _ in range(it0):
        [random.choice((-1.0, 1.0)) for _ in KEYS]

    t_start = time.time()
    print("SPSA: %d params, %d pairs/eval (%d games), opponent %s, %d workers"
          % (len(KEYS), args.pairs, 2 * args.pairs, args.opponent, args.workers))
    if it0:
        print("resuming from iteration %d" % it0)

    for k in range(it0, it0 + args.iters):
        elapsed_min = (time.time() - t_start) / 60.0
        if args.budget_min and elapsed_min > args.budget_min:
            print("wall-clock budget (%.0f min) reached — stopping at iteration %d"
                  % (args.budget_min, k))
            break

        delta = [random.choice((-1.0, 1.0)) for _ in KEYS]
        xp = [min(X_CLIP, max(-X_CLIP, x[i] + C_PERTURB * delta[i])) for i in range(len(KEYS))]
        xm = [min(X_CLIP, max(-X_CLIP, x[i] - C_PERTURB * delta[i])) for i in range(len(KEYS))]
        tp, tm = x_to_theta(xp), x_to_theta(xm)

        wp = write_weights(WEIGHT_DIR / ("iter%03d_plus.json" % k), tp)
        wm = write_weights(WEIGHT_DIR / ("iter%03d_minus.json" % k), tm)

        seed0 = args.seed + k * args.pairs
        seeds = list(range(seed0, seed0 + args.pairs))

        jobs = (build_jobs("plus", wp, seeds, args.opponent, args.timeout, args.chunk)
                + build_jobs("minus", wm, seeds, args.opponent, args.timeout, args.chunk))
        t0 = time.time()
        agg = tally(execute(jobs, args.workers))
        dt = time.time() - t0

        yp, ym = agg["plus"]["rate"], agg["minus"]["rate"]
        ghat = [(yp - ym) / (2.0 * C_PERTURB * delta[i]) for i in range(len(KEYS))]
        step = [max(-STEP_CLIP, min(STEP_CLIP, A_GAIN * g)) for g in ghat]
        x_next = [min(X_CLIP, max(-X_CLIP, x[i] + step[i])) for i in range(len(KEYS))]

        rec = {
            "kind": "spsa_iter", "iter": k, "ts": time.time(),
            "seeds": [seeds[0], seeds[-1]], "pairs": args.pairs,
            "opponent": args.opponent, "timeout": args.timeout,
            "c": C_PERTURB, "a": A_GAIN,
            "keys": KEYS, "x": x, "delta": delta,
            "y_plus": yp, "y_minus": ym,
            "plus": agg["plus"], "minus": agg["minus"],
            "ghat_scale": (yp - ym) / (2.0 * C_PERTURB),
            "x_next": x_next, "theta_next": x_to_theta(x_next),
            "seconds": dt,
        }
        log_rec(rec)
        print("iter %2d  y+ %5.3f (%2d/%2d)  y- %5.3f (%2d/%2d)  d=%+0.3f  %4.1f min"
              % (k, yp, agg["plus"]["wins"], agg["plus"]["games"],
                 ym, agg["minus"]["wins"], agg["minus"]["games"], yp - ym, dt / 60.0))
        x = x_next

    # Polyak-style tail average of the iterates: SPSA iterates are noisy, the
    # running average of the last few is the standard estimate to deploy.
    _, _, _, recs = load_state()
    iters = [r for r in recs if r.get("kind") == "spsa_iter"]
    tail = [r["x_next"] for r in iters][-args.tail:]
    x_avg = [sum(t[i] for t in tail) / len(tail) for i in range(len(KEYS))]
    theta_avg = x_to_theta(x_avg)
    out = RESULT_DIR / "tuned-final.json"
    write_weights(out, theta_avg)
    log_rec({"kind": "final", "ts": time.time(), "tail": len(tail),
             "x": x_avg, "theta": theta_avg, "path": str(out)})
    print("\nfinal (tail-avg over last %d iterates) written to %s" % (len(tail), out))
    for kk in KEYS:
        r = theta_avg[kk] / DEFAULTS[kk]
        if abs(math.log(r)) > 0.08:
            print("   %-28s %10.4f -> %10.4f  (x%.2f)" % (kk, DEFAULTS[kk], theta_avg[kk], r))


def cmd_validate(args):
    seeds = list(range(args.seed, args.seed + args.pairs))
    jobs = []
    for spec in args.candidates:
        label = spec
        wp = None if spec == "default" else Path(spec).resolve()
        if wp is not None and not wp.exists():
            sys.exit("no such weight file: %s" % wp)
        jobs += build_jobs(label, wp, seeds, args.opponent, args.timeout, args.chunk)
    t0 = time.time()
    agg = tally(execute(jobs, args.workers))
    print("\n=== validation vs %s, seeds %d..%d, %d mirror pairs, %.1f min ==="
          % (args.opponent, seeds[0], seeds[-1], args.pairs, (time.time() - t0) / 60.0))
    out = {"kind": "validation", "ts": time.time(), "opponent": args.opponent,
           "seeds": [seeds[0], seeds[-1]], "pairs": args.pairs, "results": {}}
    for label, a in agg.items():
        lo, hi = wilson(a["wins"], a["games"])
        print("%-40s %2d/%2d = %5.1f%%  [95%% CI %.1f-%.1f%%]   P1 %d/%d  P2 %d/%d  draws %d  other %d"
              % (label, a["wins"], a["games"], 100 * a["rate"], 100 * lo, 100 * hi,
                 a["as_p1"], args.pairs, a["as_p2"], args.pairs, a["draws"], a["other"]))
        out["results"][label] = {**a, "ci": [lo, hi]}
    log_rec(out)


def cmd_report(args):
    """Human-readable summary of the tuning log (for docs/riptide-v2.md)."""
    _, _, _, recs = load_state()
    iters = [r for r in recs if r.get("kind") == "spsa_iter"]
    if not iters:
        print("no iterations logged")
        return
    print("iter  seeds        y+            y-           delta    min   |x|max")
    tot = 0.0
    for r in iters:
        n = r["plus"]["games"]
        xmax = max(abs(v) for v in r["x_next"])
        tot += r["seconds"]
        print("%4d  %5d-%-5d %5.3f (%2d/%2d)  %5.3f (%2d/%2d)  %+6.3f  %4.1f  %5.2f"
              % (r["iter"], r["seeds"][0], r["seeds"][1],
                 r["y_plus"], r["plus"]["wins"], n,
                 r["y_minus"], r["minus"]["wins"], n,
                 r["y_plus"] - r["y_minus"], r["seconds"] / 60.0, xmax))
    ys = [r["y_plus"] for r in iters] + [r["y_minus"] for r in iters]
    n = iters[0]["plus"]["games"]
    print("\n%d iterations, %d games, %.1f min total" % (len(iters), 2 * n * len(iters), tot / 60.0))
    print("all evaluated candidates: mean win rate %.3f, min %.3f, max %.3f"
          % (sum(ys) / len(ys), min(ys), max(ys)))
    # Noise floor: sd of a single 32-game win rate at the observed mean.
    p = sum(ys) / len(ys)
    se = math.sqrt(p * (1 - p) / n)
    print("single-evaluation SE at p=%.3f, n=%d: %.3f  (SE of y+ - y- <= %.3f)"
          % (p, n, se, se * math.sqrt(2)))
    ds = [r["y_plus"] - r["y_minus"] for r in iters]
    obs = math.sqrt(sum(d * d for d in ds) / len(ds))
    print("observed RMS(y+ - y-) = %.3f — signal only if this exceeds the SE above" % obs)


def cmd_dump(args):
    write_weights(Path(args.out), dict(DEFAULTS))
    print("wrote %d defaults to %s" % (len(DEFAULTS), args.out))


def cmd_check(args):
    """Verify DEFAULTS above still matches the compiled-in C# defaults."""
    src = (ROOT / "bot/RiptideEval.cs").read_text()
    found = dict(re.findall(r'Weights\.G\("([\w.]+)",\s*(-?[\d.]+)\)', src))
    bad = 0
    for k, v in DEFAULTS.items():
        if k not in found:
            print("MISSING in C#: %s" % k); bad += 1
        elif abs(float(found[k]) - v) > 1e-9:
            print("MISMATCH %s: py=%s cs=%s" % (k, v, found[k])); bad += 1
    for k in found:
        if k not in DEFAULTS:
            print("NOT TUNED (C# only): %s" % k)
    print("check: %d parameters, %d problems" % (len(DEFAULTS), bad))
    return 1 if bad else 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)

    common = dict(pairs=16, seed=3000, workers=10, timeout=10, chunk=2, opponent="BestMCTS3")

    r = sub.add_parser("run")
    r.add_argument("--iters", type=int, default=10)
    r.add_argument("--pairs", type=int, default=common["pairs"])
    r.add_argument("--seed", type=int, default=common["seed"])
    r.add_argument("--workers", type=int, default=common["workers"])
    r.add_argument("--timeout", type=int, default=common["timeout"])
    r.add_argument("--chunk", type=int, default=common["chunk"])
    r.add_argument("--opponent", default=common["opponent"])
    r.add_argument("--budget-min", type=float, default=0.0)
    r.add_argument("--rng-seed", type=int, default=20260731)
    r.add_argument("--tail", type=int, default=3)
    r.set_defaults(func=cmd_run)

    v = sub.add_parser("validate")
    v.add_argument("--candidates", nargs="+", required=True,
                   help="'default' or paths to weight files")
    v.add_argument("--pairs", type=int, default=common["pairs"])
    v.add_argument("--seed", type=int, default=9000)
    v.add_argument("--workers", type=int, default=common["workers"])
    v.add_argument("--timeout", type=int, default=common["timeout"])
    v.add_argument("--chunk", type=int, default=common["chunk"])
    v.add_argument("--opponent", default=common["opponent"])
    v.set_defaults(func=cmd_validate)

    d = sub.add_parser("dump")
    d.add_argument("--out", default=str(WEIGHT_DIR / "baseline.json"))
    d.set_defaults(func=cmd_dump)

    c = sub.add_parser("check")
    c.set_defaults(func=cmd_check)

    rp = sub.add_parser("report")
    rp.set_defaults(func=cmd_report)

    args = ap.parse_args()
    sys.exit(args.func(args) or 0)


if __name__ == "__main__":
    main()
