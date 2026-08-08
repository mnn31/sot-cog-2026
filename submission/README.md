# Submission packaging

How `RiptideSubmission.cs` is produced, what the packaging step changes, and how
to rename the agent before sending it off.

The organizers accept **either** a single `.cs` file **or** a zip of all needed
files, up to 25 MB. We ship the single file — it compiles into their
`ScriptsOfTribute-Core/Bots` project unchanged.

---

## Regenerating the merged file

```bash
python3 submission/pack.py            # writes submission/RiptideSubmission.cs
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
the file each block came from. Usings are deduplicated and sorted; `namespace
Riptide;` appears exactly once.

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

**Comment rewrites.** A handful of comments referring to the tuning harness are
rewritten so the shipped file does not describe machinery it no longer contains.
Every one is an exact-text anchor asserted to match exactly once
(`EDITS` in `pack.py`), and `pack.py` refuses to emit a file still containing
`RIPTIDE_WEIGHTS`, `RIPTIDE_DIAG`, `GetEnvironmentVariable`, `File.ReadAllText`,
`DllImport`, `stackalloc` or `unsafe`.

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

The final competition name is still TBD. The class name is what `GameRunner`
matches on (`Program.cs` reflects over the `Bots` assembly and selects by type
name), so renaming the agent means renaming the type.

```bash
sed -i '' 's/\bRiptide\b/NewName/g' submission/RiptideSubmission.cs
mv submission/RiptideSubmission.cs submission/NewName.cs
```

That one substitution covers the class, its constructor, the namespace and the
prose comments together — they all use the same capitalized token.

Two things it does **not** catch, both cosmetic:

- the lowercase `[riptide]` prefix on the fatal-outcome stderr line, and
- the lowercase `riptide` in the `docs/riptide-v2.md` citations in comments.

Fix those with a second `s/riptide/newname/g` if we care. Also update `NAMESPACE`
and `ORDER`/`HEADER` in `pack.py`, or the rename will be undone the next time the
file is regenerated.

Renaming the file itself is free — nothing references the filename.

---

## Verifying a candidate submission

The merged file is validated by dropping it into the Bots project as the *only*
Riptide source and building:

```bash
# from the repo root
mv ScriptsOfTribute-Core/Bots/src/Riptide /tmp/riptide_src_backup
cp submission/RiptideSubmission.cs ScriptsOfTribute-Core/Bots/src/
cd ScriptsOfTribute-Core && dotnet build TalesOfTribute.sln -c Release
```

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
DOTNET_ROLL_FORWARD=LatestMajor dotnet GameRunner.dll Riptide BestMCTS3 -n 1 -s 42 -to 10
```

Look for `Ends due to other factors: 0/1` and no stderr output.

**Restore the tree afterwards:**

```bash
rm ScriptsOfTribute-Core/Bots/src/RiptideSubmission.cs
mv /tmp/riptide_src_backup ScriptsOfTribute-Core/Bots/src/Riptide
scripts/sync_bot.sh          # rebuilds and re-stages the normal dev DLLs
```

`ScriptsOfTribute-Core/` is gitignored, so `git status` will not warn you if you
forget.

---

## Pre-send checklist

- [ ] `python3 submission/pack.py --check` passes
- [ ] merged file builds clean in the Bots project as the only Riptide source
- [ ] zero warnings attributable to the merged file
- [ ] a game vs `BestMCTS3` ends with `other factors: 0` and silent stderr
- [ ] file size well under 25 MB (it is ~91 KB)
- [ ] **any promoted tuned weights are baked into `bot/*.cs` literals, and the
      final benchmark was run with `RIPTIDE_WEIGHTS` unset** (see the trap above)
- [ ] agent renamed, `DESCRIPTION.md` placeholders filled in
- [ ] `DESCRIPTION.md` TODO slots (tuned weights, benchmark numbers) resolved
- [ ] decided whether to soften the competitor-analysis comments and drop the
      internal `docs/riptide-v*.md` citations from the shipped comments
