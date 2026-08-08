#!/usr/bin/env python3
"""Merge bot/*.cs into the single-file competition submission.

Produces submission/RiptideSubmission.cs: every type from bot/ in one file, one
set of usings, one namespace, with two deliberate semantic changes for the
submission form:

  1. The external weight-file override machinery (RIPTIDE_WEIGHTS) is REMOVED,
     not merely disabled. Weights.G becomes a pass-through that returns the
     compiled-in default, so the agent reads no files and consults no
     environment variables and therefore has no start-up failure mode.
  2. The diagnostic stderr instrumentation (RIPTIDE_DIAG) is gated off with a
     compile-time `const bool Diag = false` instead of an environment lookup.

Every edit is anchored on exact source text and asserted: if bot/*.cs changes
in a way that moves an anchor, this script fails loudly rather than silently
shipping the tuning machinery.

Usage:  python3 submission/pack.py [--check]
        --check verifies the committed .cs is up to date instead of writing it.
"""
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "bot"
OUT = ROOT / "submission" / "RiptideSubmission.cs"

# Emission order: entry point first, then the machinery it calls, so the file
# reads top-down for someone opening it cold.
ORDER = [
    "Riptide.cs",
    "RiptideSearch.cs",
    "RiptidePolicy.cs",
    "RiptideEval.cs",
    "RiptideCards.cs",
    "RiptideWeights.cs",
]

NAMESPACE = "Riptide"

# Usings dropped from the merged file because the code that needed them is the
# code we strip (System.Globalization was only used by the weight-file parser).
DROP_USINGS = {"using System.Globalization;"}

# Explicit usings added even though Bots.csproj sets ImplicitUsings=enable, so
# the file also compiles in a project that does not.
EXTRA_USINGS = ["using System;", "using System.Collections.Generic;"]

HEADER = """// ---------------------------------------------------------------------------
// Riptide — IEEE CoG 2026 Tales of Tribute AI Competition entry.
//
// Single-file submission build. Generated from the development tree by
// submission/pack.py; the six source files it merges are, in emission order:
//
//     Riptide.cs         AI entry point, patron draft, turn clock
//     RiptideSearch.cs   determinized-ensemble turn-plan search (PlanNode, Search)
//     RiptidePolicy.cs   move-ordering priors / rollout policy (PolicyContext, Policy)
//     RiptideEval.cs     leaf evaluation (Eval)
//     RiptideCards.cs    card valuation tables (CardValues)
//     RiptideWeights.cs  knob table (Weights)
//
// Builds against the ScriptsOfTribute-Core Bots project as-is
// (netstandard2.1, LangVersion 10, ImplicitUsings + Nullable enabled).
//
// Two deliberate differences from the development tree, both applied by the
// packaging step so the submitted agent has no external dependencies at all:
//
//   * The evaluation/search knobs can be overlaid from an external JSON file in
//     the development tree, which is how they were tuned. That machinery is
//     removed here: Weights.G returns the compiled-in default unconditionally,
//     so the agent opens no files and reads no environment variables, and the
//     literal in each Weights.G(key, default) call below is exactly the value
//     that plays.
//   * The per-turn diagnostic stderr trace is switched off at the source
//     (Diag = false, with no way to switch it on short of a recompile). The
//     only thing this agent ever writes to stderr is a single line when it
//     loses a game by its own timeout or exception, which is there so a
//     regression cannot hide inside an aggregate result.
//
// Both switches are `static readonly` rather than `const` on purpose: the JIT
// folds a static readonly bool/int just the same, but the C# compiler does not
// treat the guarded blocks as unreachable, so this file builds warning-free.
//
// No file I/O, no environment variables, no process spawning, no threads of its
// own, no native interop, no unsafe code, no reflection over external data.
// ---------------------------------------------------------------------------
"""

# --- canned replacement for the whole Weights type -------------------------
# The dev-tree Weights reads an environment variable, opens a file and parses
# it. Nothing of that survives packaging. Anything outside RiptideWeights.cs
# that touches a Weights member not provided here makes pack.py fail (see
# check_weights_surface).
WEIGHTS_SUBMISSION = '''/// <summary>
/// The knob table behind every tunable number in <see cref="Eval"/>,
/// <see cref="CardValues"/> and <see cref="Search"/>.
///
/// In the development tree this type can overlay the compiled-in defaults from
/// an external JSON file, which is how the values were tuned. The submission
/// build removes that entirely: <see cref="G"/> is a pass-through, so the
/// literal in every <c>Weights.G(key, default)</c> call in this file is the
/// value that ships. The agent consequently opens no files, reads no
/// environment variables, and cannot fail to start.
/// </summary>
public static class Weights
{
    /// <summary>Number of active overrides. Always zero in the submission build.
    /// Deliberately `static readonly` and not `const`: the JIT folds it either
    /// way, but as a compile-time constant it would make the body of
    /// <c>CardValues.ApplyOverrides</c> formally unreachable and warn.</summary>
    public static readonly int Count = 0;

    /// <summary>Returns the compiled-in default for <paramref name="key"/>.</summary>
    public static double G(string key, double def)
    {
        return def;
    }

    private static int _announced;

    /// <summary>
    /// Forces <see cref="Eval"/> and <see cref="CardValues"/> to run their
    /// static constructors once, up front, so that work does not land inside
    /// the first timed decision of the first game.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0) return;
        _ = Eval.Scale + CardValues.Deck[0] + CardValues.ComboFactor[0];
    }
}
'''

# Members the merged file is allowed to reference on Weights from outside
# RiptideWeights.cs. SourcePath and Error exist in the dev tree and are
# deliberately absent here.
WEIGHTS_ALLOWED_MEMBERS = {"G", "Count", "EnsureLoaded"}

# --- anchored edits ---------------------------------------------------------
# (source file, exact text to find, replacement). Every one must match exactly
# once.
EDITS = [
    (
        "Riptide.cs",
        """    // Off unless RIPTIDE_DIAG=1. Used to verify we are actually spending the
    // turn budget (and never overrunning it) without touching the hot path.
    private static readonly bool Diag =
        Environment.GetEnvironmentVariable("RIPTIDE_DIAG") == "1";""",
        """    // Switched off for the submission. In the development tree this is bound
    // to an environment variable and prints one per-turn budget/throughput line
    // to stderr; here it is a false constant folded away by the JIT, so the
    // agent emits no diagnostic output at all and there is no way to turn it on
    // without a recompile. (`static readonly`, not `const`, only so the block
    // below does not trip an unreachable-code warning.)
    private static readonly bool Diag = false;""",
    ),
    (
        "Riptide.cs",
        """        // Load the external weight table (RIPTIDE_WEIGHTS) exactly once per
        // process, before any search runs, and force Eval/CardValues to
        // initialise so a malformed file fails loudly here rather than silently
        // reverting to defaults halfway through a tournament.
        Weights.EnsureLoaded();""",
        """        // Force Eval/CardValues to run their static constructors once, here,
        // rather than inside the first timed decision of the first game.
        Weights.EnsureLoaded();""",
    ),
    (
        "RiptideEval.cs",
        """    // These are `static readonly` rather than `const` so that every one of them
    // can be overridden from the RIPTIDE_WEIGHTS file (see Weights). The JIT
    // still folds a static readonly double after class init, so the hot path is
    // unchanged. The literal in each G(...) call is the compiled-in default and
    // is what ships when no weight file is set.""",
        """    // Each knob is read through Weights.G(key, default). In the submission build
    // Weights.G is a pass-through, so the literal in each call below IS the
    // value that ships. They stay `static readonly` rather than `const` because
    // the development tree's tuning harness rebinds them; the JIT folds a
    // static readonly double after class init either way, so the hot path is
    // identical.""",
    ),
    (
        "RiptideCards.cs",
        """    /// <summary>
    /// Overlay RIPTIDE_WEIGHTS overrides on top of the compiled-in tables.
    /// Runs once, inside the static constructor, after every default is set.
    /// Keys are accepted by CardId name ("card.RALLY.deck") and by raw index
    /// ("card.68.deck"); the index form is applied second so it wins.
    /// </summary>""",
        """    /// <summary>
    /// Overlay tuner overrides on top of the compiled-in tables. Inert in the
    /// submission build, where Weights.Count is always zero so this returns
    /// immediately; kept so the shipped tables come out of the same code path
    /// the tuning runs used.
    /// </summary>""",
    ),
    (
        "RiptideSearch.cs",
        """    // Selected through the weight file so A/B costs no rebuild:
    //   search.Aggregation = 0  mean over worlds                       [default]""",
        """    // Fixed at pack time in the submission build (the development tree binds it
    // from the tuning harness instead, so an A/B costs no rebuild):
    //   search.Aggregation = 0  mean over worlds                       [SHIPPED]""",
    ),
]

# Tokens that must not survive into the submission file.
FORBIDDEN = [
    "RIPTIDE_WEIGHTS",
    "RIPTIDE_DIAG",
    "GetEnvironmentVariable",
    "File.ReadAllText",
    "System.Diagnostics.Process",
    "DllImport",
    "stackalloc",
    "unsafe",
]

USING_RE = re.compile(r"^using\s+[\w.]+\s*;\s*$")


def fail(msg):
    print("pack.py: FAIL: " + msg, file=sys.stderr)
    sys.exit(1)


def split_file(text, name):
    """Return (usings, body) with the `namespace X;` line removed."""
    lines = text.split("\n")
    usings = [l.strip() for l in lines if USING_RE.match(l)]
    ns_idx = None
    for i, l in enumerate(lines):
        if l.strip() == "namespace %s;" % NAMESPACE:
            ns_idx = i
            break
    if ns_idx is None:
        fail("%s has no `namespace %s;` line" % (name, NAMESPACE))
    body = lines[ns_idx + 1:]
    while body and not body[0].strip():
        body.pop(0)
    while body and not body[-1].strip():
        body.pop()
    return usings, "\n".join(body)


def check_weights_surface(bodies):
    """Every Weights.<member> reference outside RiptideWeights.cs must be one
    the submission Weights class still provides."""
    used = set()
    for name, body in bodies.items():
        if name == "RiptideWeights.cs":
            continue
        used.update(re.findall(r"\bWeights\.(\w+)", body))
    extra = used - WEIGHTS_ALLOWED_MEMBERS
    if extra:
        fail("code references Weights members the submission build drops: %s. "
             "Update WEIGHTS_SUBMISSION in pack.py." % sorted(extra))


def build():
    bodies = {}
    usings = []
    for name in ORDER:
        p = SRC / name
        if not p.exists():
            fail("missing source %s" % p)
        text = p.read_text(encoding="utf-8")
        u, body = split_file(text, name)
        for x in u:
            if x not in usings and x not in DROP_USINGS:
                usings.append(x)
        bodies[name] = body

    found = sorted(p.name for p in SRC.glob("*.cs"))
    if found != sorted(ORDER):
        fail("bot/ contains %s but ORDER lists %s" % (found, sorted(ORDER)))

    for name, needle, repl in EDITS:
        n = bodies[name].count(needle)
        if n != 1:
            fail("anchor for edit in %s matched %d times (expected 1):\n%s"
                 % (name, n, needle[:120]))
        bodies[name] = bodies[name].replace(needle, repl)

    # Whole-type replacement: the dev Weights never reaches the submission.
    check_weights_surface(bodies)
    bodies["RiptideWeights.cs"] = WEIGHTS_SUBMISSION.rstrip("\n")

    for x in EXTRA_USINGS:
        if x not in usings:
            usings.insert(0, x)
    def ns_key(u):
        # Sort on the namespace itself so `using System;` precedes
        # `using System.Diagnostics;` (raw line sort puts ';' after '.').
        return u[len("using "):-1].split(".")

    sys_u = sorted((u for u in usings if u.startswith("using System")), key=ns_key)
    other_u = sorted((u for u in usings if not u.startswith("using System")), key=ns_key)

    parts = [HEADER, ""]
    parts.extend(sys_u)
    parts.append("")
    parts.extend(other_u)
    parts.append("")
    parts.append("namespace %s;" % NAMESPACE)
    for name in ORDER:
        parts.append("")
        parts.append("// " + "=" * 74)
        parts.append("// from %s" % name)
        parts.append("// " + "=" * 74)
        parts.append("")
        parts.append(bodies[name])
    out = "\n".join(parts).rstrip("\n") + "\n"

    # Scan the merged code, not the header (which names these tokens in order
    # to state that the code does not use them).
    scanned = out[len(HEADER):]
    for tok in FORBIDDEN:
        if tok in scanned:
            fail("forbidden token %r survived into the merged file" % tok)
    if out.count("namespace ") != 1:
        fail("expected exactly one namespace declaration")
    if out.count("public class Riptide : AI") != 1:
        fail("expected exactly one `public class Riptide : AI`")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="verify the existing file matches instead of writing")
    args = ap.parse_args()
    out = build()
    if args.check:
        if not OUT.exists():
            fail("%s does not exist" % OUT)
        if OUT.read_text(encoding="utf-8") != out:
            fail("%s is stale — rerun `python3 submission/pack.py`" % OUT)
        print("pack.py: %s is up to date (%d bytes)" % (OUT.name, len(out.encode())))
        return
    OUT.write_text(out, encoding="utf-8")
    print("pack.py: wrote %s (%d bytes, %d lines)"
          % (OUT, len(out.encode()), out.count("\n")))


if __name__ == "__main__":
    main()
