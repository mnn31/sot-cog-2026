# Submission packaging

How `PrimalKyogre.cs` is produced and what the packaging step changes.

The agent's development name is `Riptide` (that is still what `bot/*.cs` and the
dev-tree Bots project use). The **competition name is Primal Kyogre**, and the
rename is applied by `pack.py`, not by hand — see "Renaming the agent" below.

The organizers accept **either** a single `.cs` file **or** a zip of all needed
files, up to 25 MB. We ship the single file — it compiles into their
`ScriptsOfTribute-Core/Bots` project unchanged.

---

## Regenerating the merged file

```bash
python3 submission/pack.py            # writes submission/PrimalKyogre.cs
python3 submission/pack.py --check    # fails if the file is stale vs bot/*.cs
```

`pack.py` merges all six sources in `bot/` into one file:

| source | types |
|---|---|
| `bot/Riptide.cs` | `Riptide` (the `AI` subclass), patron draft, turn clock |
| `bot/RiptideSearch.cs` | `PlanNode`, `Search` |
| `bot/RiptidePolicy.cs` | `PolicyContext`, `Policy` |
| `bot/RiptideEval.cs` | `Eval` |
| `bot/RiptideCards.cs` | `CardValues` |
| `bot/RiptideWeights.cs` | `Weights` |

They are emitted in that order (entry point first) with `// ===` banners naming
the file each block came from. Usings are deduplicated and sorted; the namespace
appears exactly once. Everything is then renamed to the competition name, so the
banners read `PrimalKyogre.cs`, `PrimalKyogreSearch.cs`, … in the shipped file.

`using System;` and `using System.Collections.Generic;` are added explicitly
even though `Bots.csproj` sets `ImplicitUsings=enable`, so the file also
compiles in a project that does not.

Run `pack.py --check` in CI or before any submission so a late edit in `bot/`
cannot ship as a stale merged file.

---

## What the packaging step strips

Two changes, both applied by `pack.py`, both about removing anything the
organizers' environment would have to cooperate with.

**1. The external weight-file override machinery is removed, not disabled.**

In the dev tree, `Weights` reads the `RIPTIDE_WEIGHTS` environment variable, and
if it is set, opens that path and parses a flat JSON table that overlays every
tunable in `Eval`, `CardValues` and `Search`. That is how the numbers were
tuned, and it is exactly the kind of thing that turns into a crash on someone
else's machine.

`pack.py` replaces the whole `Weights` type with a pass-through:

```csharp
public static readonly int Count = 0;
public static double G(string key, double def) { return def; }
```

so every `Weights.G("eval.DeckWeight", 1.8)` call site in the merged file
provably yields its literal. The submitted agent opens no file and reads no
environment variable, so it has no start-up failure mode at all.

The replacement is guarded: `check_weights_surface()` scans every other merged
body for `Weights.<member>` references and fails the pack if any member the
submission build drops (`SourcePath`, `Error`, `Parse`, …) is still used. Add a
member to `Weights` and use it outside `RiptideWeights.cs`, and `pack.py` stops
rather than silently shipping a broken file.

**2. `RIPTIDE_DIAG` stderr instrumentation is gated off.**

```csharp
private static readonly bool Diag = Environment.GetEnvironmentVariable("RIPTIDE_DIAG") == "1";
    -->
private static readonly bool Diag = false;
```

The per-turn budget/throughput trace in `RollTurn()` is left in the source (it
documents what the time manager is doing) but can never fire.

`static readonly` rather than `const` is deliberate in both places. The JIT
folds a static readonly `bool`/`int` just the same, but a compile-time constant
would make the guarded blocks formally unreachable and the build would emit two
`CS0162` warnings. As written, the file compiles with **zero** warnings.

**What is deliberately NOT stripped:** the single stderr line in `GameEnd()`
that fires only when we lose a game to our own `TURN_TIMEOUT` / `BOT_EXCEPTION`
/ `INCORRECT_MOVE`. It is one line, only on a self-inflicted loss, and it is the
only way a timing regression shows up as anything other than an unexplained
number in an aggregate result. In the two validation games it printed nothing.

**3. Comments are scrubbed for publication.**

The shipped file gets published and read by other entrants, so its comments are
rewritten for a reader who has never seen this repository. Three classes go:

| class | example in the dev tree | what ships |
|---|---|---|
| internal doc citations | `See docs/riptide-v2.md §4.2.` | dropped; the claim stands on its own |
| dev-diary voice | `v1 used a flat slice sized on a fast Mac`, `truncation is where the pod regression came from`, `v2.1:` section markers | restated as a property of the code, version- and host-free |
| pointed competitor commentary | `the biggest hole in the 2025 winner`, `the 2025 field is structurally blind to it` | design rationale kept and reworded as a fact about *this* agent; the judgement of other people's work dropped |

Comments explaining **what** the code does and **why** a design was chosen are
kept verbatim — the file is still meant to be readable.

Every rewrite is an exact-text anchor asserted to match exactly once (`SCRUB` in
`pack.py`, same contract as `EDITS`), so this is reproducible from `bot/*.cs`
rather than hand-edited. `pack.py` then re-scans the merged body against
`SCRUB_FORBIDDEN` — regexes for `docs/*.md`, `\bv1|v2\b`, `2025`, competitor bot
names, `champion`, host names — and **fails the pack** if a newly written comment
in `bot/` reintroduces any of them. It also refuses to emit a file still
containing `RIPTIDE_WEIGHTS`, `RIPTIDE_DIAG`, `GetEnvironmentVariable`,
`File.ReadAllText`, `DllImport`, `stackalloc` or `unsafe`.

---

## ⚠ The tuned-weights trap — read before submitting

Because the packaging step **removes the JSON weight reader**, the submitted
agent plays with whatever literals are written into `bot/*.cs`. Nothing else.

Right now that is correct: no tuned weight set has been promoted. `weights/`
holds only SPSA perturbation candidates (`iterNNN_plus/minus.json`) and A/B
config stubs (`agg*.json`, `strat-on*.json`), and `docs/benchmarks.md` lists the
tuned line ("Wartortle") as **pending**. The compiled-in defaults are exactly
the benchmarked "Squirtle v1" configuration, so the merged file ships the bot we
actually measured.

**The moment a tuned set is promoted, this stops being true silently.** A tuning
run measures the bot with `RIPTIDE_WEIGHTS` pointed at a JSON file; the
submission has no such mechanism. If the winning numbers are not written back
into the `Weights.G(key, default)` literals in `bot/*.cs`, the benchmark numbers
in `DESCRIPTION.md` will describe a configuration that is not the one submitted.
`pack.py` cannot detect this — it has no idea which JSON was used.

The same applies to the search-mode A/B stubs. These ship as their compiled-in
defaults regardless of any JSON:

| knob | shipped value |
|---|---|
| `search.Aggregation` | `0` (mean over worlds) |
| `search.Stratify` | `0` (i.i.d. seeds, not stratified) |
| `search.DisagreementLambda` | `1.0` (unused at `Aggregation = 0`) |
| `search.BreadthBonus` | `0.0005` |
| `time.SliceBoost` | `1.50` |

So: **promoting a tuned config means editing the defaults in `bot/*.cs`, then
re-running `pack.py`, then re-benchmarking with no `RIPTIDE_WEIGHTS` set** to
confirm the compiled-in build reproduces the tuned result.

---

## Renaming the agent

**Done — the competition name is `PrimalKyogre` ("Primal Kyogre"), applied by
`pack.py`.** `bot/*.cs` still uses the development name; nothing in the dev tree
had to change.

The class name is what `GameRunner` matches on (`Program.cs` reflects over the
`Bots` assembly and selects by type name), so renaming the agent means renaming
the type. `pack.py` does it as the last step, on the merged body only (the
header is already written in the competition name):

```python
AGENT_CLASS      = "PrimalKyogre"    # class, namespace, output filename
AGENT_DISPLAY    = "Primal Kyogre"   # header prose
AGENT_STDERR_TAG = "primal-kyogre"   # lowercase log prefix
RENAMES = [("Riptide", AGENT_CLASS), ("riptide", AGENT_STDERR_TAG)]
```

The capitalized substitution covers the class, its constructor, the namespace,
the `// from …` module banners and the prose comments together. **The trap is
the lowercase token**: a capitalized-only rename leaves `[riptide]` on the two
stderr lines. `pack.py` handles it with the second rule and guards it three
ways:

- before renaming, the merged body must contain **exactly two** lowercase
  `riptide` occurrences (`EXPECTED_LOWER_TAGS`) — the two stderr prefixes and
  nothing else, so a new log line or a surviving doc citation trips the check;
- after renaming, `re.search(r"[Rr]iptide", out, re.IGNORECASE)` must find
  nothing at all;
- after renaming, the output must contain exactly one `public class
  PrimalKyogre : AI`, one `namespace PrimalKyogre;` and two `[primal-kyogre]`
  prefixes.

To rename again, change the three constants at the top of `pack.py` and re-run
it. Nothing else needs touching, and nothing references the output filename.

---

## Verifying a candidate submission

The merged file is validated by dropping it into the Bots project as the *only*
source from this project and building:

```bash
# from the repo root
mv ScriptsOfTribute-Core/Bots/src/Riptide /tmp/riptide_src_backup
cp submission/PrimalKyogre.cs ScriptsOfTribute-Core/Bots/src/
cd ScriptsOfTribute-Core && dotnet build TalesOfTribute.sln -c Release
```

Hash the tree first (`ScriptsOfTribute-Core/Bots/src/**` plus the three staged
`Bots.dll` copies) and again after restoring, so "restored" is proved rather
than assumed. Back the DLLs up by copy — restoring them by rebuild is a weaker
claim.

A clean build **is** the toolchain check. `Bots.csproj` pins
`LangVersion=10` and `TargetFramework=netstandard2.1`, and the compiler
enforces both: a C# 11+ construct is a hard error at that langversion, and an
API outside netstandard2.1 does not exist in the reference assemblies the build
compiles against. So building green here means the file is within the
organizers' older `dotnet-sdk-7.0` toolchain, even though our dev SDK is much
newer.

Then stage the DLL and play a couple of games:

```bash
cp ScriptsOfTribute-Core/Bots/bin/Release/netstandard2.1/Bots.dll \
   ScriptsOfTribute-Core/GameRunner/bin/Release/net8.0/Bots/Bots.dll
cd ScriptsOfTribute-Core/GameRunner/bin/Release/net8.0
DOTNET_ROLL_FORWARD=LatestMajor dotnet GameRunner.dll PrimalKyogre BestMCTS3 -n 1 -s 42 -to 10
```

Look for `Ends due to other factors: 0/1` and no stderr output.

**Restore the tree afterwards:**

```bash
rm ScriptsOfTribute-Core/Bots/src/PrimalKyogre.cs
mv /tmp/riptide_src_backup ScriptsOfTribute-Core/Bots/src/Riptide
# restore the three staged Bots.dll copies from the byte backups, or:
scripts/sync_bot.sh          # rebuilds and re-stages the normal dev DLLs
```

`ScriptsOfTribute-Core/` is gitignored, so `git status` will not warn you if you
forget.

---

## Pre-send checklist

Status as of the final packaging pass (2026-08-07):

- [x] `python3 submission/pack.py --check` passes
- [x] merged file builds clean in the Bots project as the only source from this
      project — 0 errors, and **0 warnings** attributable to `PrimalKyogre.cs`
      (the 143 warnings in the log are all pre-existing, in the engine's own
      sample bots and the 2025 archive)
- [x] games vs `BestMCTS3` end with `other factors: 0` and silent stderr —
      3/3 seeds 77–79, all won on the 40-prestige condition
- [x] file size well under 25 MB (~91 KB)
- [x] no tuned weights promoted; `bot/` is byte-identical to git HEAD, so the
      compiled-in defaults **are** the benchmarked configuration (see the trap
      above — this is the condition that makes it true, not an assumption)
- [x] agent renamed to `PrimalKyogre` by `pack.py`, including the lowercase
      `[primal-kyogre]` stderr prefixes
- [x] comment scrub applied via `SCRUB` and enforced by `SCRUB_FORBIDDEN`
- [x] `DESCRIPTION.md` TODO slots resolved (benchmark numbers, methodology,
      shipped configuration, the "substantially different" section, AI
      disclosure)
- [x] `submission/final/` assembled

Open item for the author:

- [ ] **read Section 9 (AI tool disclosure) of `DESCRIPTION.md` and confirm it
      matches both the competition's stated disclosure requirement and what
      actually happened.** It is the one section that cannot be verified from
      the repository.
