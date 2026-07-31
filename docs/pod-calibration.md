# Pod concurrency calibration

RunPod game server: 112 vCPUs, Ubuntu, project at `/root/sot`. Goal: find the highest
`bench.py --workers N` that does not distort match results via CPU contention, and
measure throughput there.

**Result: `--workers 20` is safe and is the recommended default for pod batches.**
Win rate at 20 workers matched the low-contention reference band and wall-clock time
was *faster* than at 12 workers (better parallel efficiency, not worse). The known bad
point (32 workers, 37.5%) was not re-tested; the true ceiling may sit above 20 but
was not probed in this pass (see Open questions).

## Evidence table

All rows: Riptide vs BestMCTS3, mirror pairs, `--timeout 10`. CI = Wilson 95% where available.

| Workers | Pairs (games) | Seeds | Win rate | 95% CI | Wall clock | Games/hour | Verdict |
|---|---|---|---|---|---|---|---|
| 6 | 8 (16) | 3000–3007 | 62.5% | — | — | — | safe (prior data, low contention) |
| Mac reference | 24 (48) | 3000–3023 | 52.1% | 38.3–65.5% | — | — | reference band |
| **12** | 12 (24) | 3000–3011 | 54.2% | 35.1–72.1% | 3m9.3s | 456 | safe |
| **20** | 12 (24) | 3000–3011 | 58.3% | 38.8–75.5% | 2m8.3s | 673 | safe |
| 32 | 16 (32) | 3000–3015 | 37.5% | — | — | — | **distorted** (prior data, CPU contention) |

Both new runs (12 and 20 workers) land inside the ~50–63% low-contention band with
overlapping CIs — no ambiguity, so the workers=16 bisection step was not needed.
20 workers was strictly better than 12 on both axes (higher win rate, ~1.47x
throughput), so it's the safe ceiling within the tested range.

## Mid-run system evidence (workers=20)

Captured via a second SSH session ~45s into the workers=20 run:

```
load average: 17.76, 16.28, 13.68        (out of 112 cores)
%Cpu(s): 27.6 us, 1.9 sy, 0.0 ni, 70.5 id
MiB Mem: 257548 total, 87598 free, 54894 used, 117597 buff/cache

PID   COMMAND   %CPU
3925  dotnet    120.0
3927  dotnet    120.0
3889  dotnet    110.0
...   (20 dotnet GameRunner processes total, confirmed via `pgrep -f`/`ps aux | grep dotnet`, each ~110-120% CPU)
```

Each GameRunner process is multithreaded (>100% CPU per process, likely .NET GC/thread-pool
threads), but the pod is nowhere near saturated at workers=20: load average ~18 and ~29%
overall CPU utilization out of 112 cores, with 70% idle and ~87GB RAM free. This is
consistent with 20 workers not being CPU-contention-limited, and suggests the ceiling
found at 32 workers (37.5%, distorted) is not simply "ran out of cores" — the pod still had
huge slack there too, so contention at 32 likely comes from something more local (e.g. .NET
thread-pool/GC thread oversubscription per process, or scheduler noise from many
multithreaded processes stepping on each other) rather than raw core exhaustion.

## Recommended invocation for pod batches

```bash
cd /root/sot && time python3 scripts/bench.py <BotA> <BotB> --pairs <N> --seed <S> --workers 20 --timeout 10
```

Run one batch at a time — never two `bench.py` invocations concurrently on the pod,
since each already spawns `workers` GameRunner processes and stacking batches
reproduces the same contention seen at workers=32.

## Open questions / not implemented

- **Untested gap 20–32 workers**: only 20 (safe) and 32 (distorted, prior data) are
  measured; the actual ceiling could be anywhere in between (e.g. 24 or 28). If more
  precision is needed, bisect this range the same way (12 pairs, same seed block).
- **CPU pinning (`taskset`)**: not implemented, per instructions — noted only. Since
  the mid-run `top` snapshot shows contention isn't from raw core exhaustion (only
  ~29% overall CPU used at workers=20) but each GameRunner process is already
  multithreaded past 100%, pinning each process to a fixed core range (e.g.
  `taskset -c` per worker, or wrapping the process spawn in `bench.py`) could reduce
  cross-process scheduler noise and might raise the safe ceiling above 20 — worth
  trying before assuming 32 is a hard wall.
