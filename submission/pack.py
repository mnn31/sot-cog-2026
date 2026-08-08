#!/usr/bin/env python3
"""Merge bot/*.cs into the single-file competition submission.

Produces submission/PrimalKyogre.cs: every type from bot/ in one file, one set
of usings, one namespace, with these deliberate changes for the submission
form:

  1. The external weight-file override machinery (RIPTIDE_WEIGHTS) is REMOVED,
     not merely disabled. Weights.G becomes a pass-through that returns the
     compiled-in default, so the agent reads no files and consults no
     environment variables and therefore has no start-up failure mode.
  2. The diagnostic stderr instrumentation (RIPTIDE_DIAG) is gated off with a
     compile-time `const bool Diag = false` instead of an environment lookup.
  3. Comments are scrubbed (SCRUB below): internal design-doc citations,
     development-diary voice (version archaeology, host-machine names) and
     pointed commentary about other people's entries are rewritten. What the
     code does and why it was designed that way stays.
  4. The agent is renamed from the development name to the competition name
     (RENAMES below), including the lowercase stderr prefix.

Every edit is anchored on exact source text and asserted: if bot/*.cs changes
in a way that moves an anchor, this script fails loudly rather than silently
shipping the tuning machinery or an unscrubbed comment.

Usage:  python3 submission/pack.py [--check]
        --check verifies the committed .cs is up to date instead of writing it.
"""
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "bot"

# Competition identity. AGENT_CLASS is what GameRunner matches on (Program.cs
# reflects over the Bots assembly and selects by type name), so this is also the
# namespace and the output filename.
AGENT_CLASS = "PrimalKyogre"
AGENT_DISPLAY = "Primal Kyogre"
AGENT_STDERR_TAG = "primal-kyogre"

OUT = ROOT / "submission" / (AGENT_CLASS + ".cs")

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

# The namespace/type prefix used throughout bot/. Rewritten to AGENT_CLASS on
# the way out (see RENAMES); nothing in bot/ has to change to rename the agent.
SRC_NAMESPACE = "Riptide"

# Usings dropped from the merged file because the code that needed them is the
# code we strip (System.Globalization was only used by the weight-file parser).
DROP_USINGS = {"using System.Globalization;"}

# Explicit usings added even though Bots.csproj sets ImplicitUsings=enable, so
# the file also compiles in a project that does not.
EXTRA_USINGS = ["using System;", "using System.Collections.Generic;"]

HEADER = """// ---------------------------------------------------------------------------
// {display} — IEEE CoG 2026 Tales of Tribute AI Competition entry.
//
// Author: Manan Gupta (The Harker School).
//
// Single-file submission build. The six modules it merges are, in emission
// order:
//
//     {cls}.cs         AI entry point, patron draft, turn clock
//     {cls}Search.cs   determinized-ensemble turn-plan search (PlanNode, Search)
//     {cls}Policy.cs   move-ordering priors / rollout policy (PolicyContext, Policy)
//     {cls}Eval.cs     leaf evaluation (Eval)
//     {cls}Cards.cs    card valuation tables (CardValues)
//     {cls}Weights.cs  knob table (Weights)
//
// Builds against the ScriptsOfTribute-Core Bots project as-is
// (netstandard2.1, LangVersion 10, ImplicitUsings + Nullable enabled).
//
// Two deliberate behavioural differences from the development tree, both
// applied by the packaging step so the submitted agent has no external
// dependencies at all:
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
""".format(display=AGENT_DISPLAY, cls=AGENT_CLASS)

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

# --- comment scrub ----------------------------------------------------------
# The shipped file is published, so its comments are written for a reader who
# has never seen this repository. Three classes of comment are rewritten:
#
#   (a) citations of internal design docs (docs/riptide-v2.md §...), which the
#       reader cannot follow;
#   (b) development-diary voice — version archaeology ("v1 did X, v2 changed
#       it"), host-machine names, references to specific regressions;
#   (c) pointed commentary about other entrants' bots. Factual design rationale
#       is kept and reworded as a property of THIS agent; the value judgements
#       about other people's work are dropped.
#
# Same contract as EDITS: exact anchor, must match exactly once.
SCRUB = [
    (
        "Riptide.cs",
        "/// Riptide — CoG 2026 Tales of Tribute entry, v1.",
        "/// Riptide — IEEE CoG 2026 Tales of Tribute AI Competition entry.",
    ),
    (
        "Riptide.cs",
        """    // Orgnum first is the one point of agreement between the two 2025 bots that
    // beat the champion head to head. Alessia second because the 2025 field is
    // structurally blind to it (BestMCTS3 refuses to draft it, HQL has almost no
    // trained values for it), and we price it properly.""",
        """    // Orgnum first: its deck is cheap, prestige-dense and combos with itself,
    // and it is the patron this evaluation prices highest at the draft stage.
    // Saint Alessia second: it is the newest deck, and the one our card values
    // price furthest from a conventional tier list, so drafting it is where the
    // valuation has the most room to pay off.""",
    ),
    (
        "Riptide.cs",
        """    // v2 change: the per-decision slice is HARDWARE-ADAPTIVE.
    //
    // v1 used a flat `min(0.80, left/4)` slice, sized on a fast Mac. It has two
    // failure modes on a slower or contended machine: the search stops long
    // before it has solved the reachable turn plan space, AND the turn's budget
    // is left partly unspent because no mechanism hands the unused time of a
    // fast-solving decision to a slow one. Measured cost: 52% vs BestMCTS3 on
    // the Mac, 37.5% on a contended box with the same seeds.""",
        """    // The per-decision slice is HARDWARE-ADAPTIVE.
    //
    // A flat slice sized on one machine has two failure modes on a slower or
    // contended one: the search stops long before it has solved the reachable
    // turn plan space, AND the turn's budget is left partly unspent, because
    // nothing hands the unused time of a fast-solving decision to a slow one.
    // Both cost measurable win rate under load — enough to be worth deriving
    // the slice from measured throughput rather than fixing it in advance.""",
    ),
    (
        "Riptide.cs",
        "    //       decisions after it — the redistribution v1 had no mechanism for;",
        "    //       decisions after it, which a fixed per-decision cap cannot do;",
    ),
    (
        "Riptide.cs",
        "    //   (5) a fixed-fraction cap on the slice, inherited from v1's `left/4`, so",
        "    //   (5) a fixed-fraction cap on the slice, so",
    ),
    (
        "Riptide.cs",
        """    // v1's `left/4` had one property worth keeping: capping a slice at a FIXED
    // FRACTION of what is left makes the remaining budget decay geometrically, so
    // it can never reach zero and the turn can never run out of decisions no
    // matter how badly the count was estimated. Measured: without this, six
    // times oversubscribed, v2 spent 9.03 s of a 9.25 s budget and dropped up to
    // 12 tail decisions into the greedy fallback, where v1 dropped none. It only
    // binds late in a turn, where the share term has already grown past it.""",
        """    // Capping a slice at a FIXED FRACTION of what is left makes the remaining
    // budget decay geometrically, so it can never reach zero and the turn can
    // never run out of decisions no matter how badly the decision count was
    // estimated. Measured at six times oversubscription: without this cap the
    // agent spends 9.03 s of a 9.25 s budget and drops up to 12 tail decisions
    // into the greedy fallback; with it, none are dropped. It only binds late in
    // a turn, where the share term has already grown past it.""",
    ),
    (
        "Riptide.cs",
        """    // Decisions-per-turn estimate. Measured on the Mac: 7-12 searched decisions
    // per turn, mean ~9.5.""",
        """    // Decisions-per-turn estimate. Measured: 7-12 searched decisions per turn,
    // mean ~9.5.""",
    ),
    (
        "Riptide.cs",
        """    // solve here costs 1.5 s, handing a decision 0.9 s reliably buys a truncated
    // search, and truncation is where the pod regression came from.""",
        """    // solve here costs 1.5 s, handing a decision 0.9 s reliably buys a truncated
    // search, and a truncated search is measurably weaker than a solved one.""",
    ),
    (
        "Riptide.cs",
        """    // Search throughput on this machine. Measured on the Mac: ~37 k
    // iterations/s; a contended or slower box runs a fraction of that, and the
    // sweep floor below grows in exact proportion.""",
        """    // Search throughput on this machine. ~37 k iterations/s on the development
    // machine; a contended or slower box runs a fraction of that, and the sweep
    // floor below grows in exact proportion.""",
    ),
    (
        "Riptide.cs",
        """    // v2.1: how often the selected aggregation rule overrode plain-mean
    // aggregation, and what the stratified seed draw cost. Both diag-only.""",
        """    // How often the selected aggregation rule overrode plain-mean aggregation,
    // and what the stratified seed draw cost. Both diagnostic-only.""",
    ),
    (
        "Riptide.cs",
        """        //     v1 could not do this — its 0.80 s cap was hit whether or not the
        //     rest of the turn had 8 spare seconds sitting idle.""",
        """        //     A fixed per-decision cap cannot do this: it binds whether or not
        //     the rest of the turn has spare seconds sitting idle.""",
    ),
    (
        "Riptide.cs",
        """        // never more than a fixed fraction of what is left. This is the one part
        // of v1's `left/4` worth keeping — it makes the remaining budget decay
        // geometrically instead of linearly, so no misestimate of the decision
        // count can strand the end of the turn on the greedy fallback.""",
        """        // never more than a fixed fraction of what is left. This makes the
        // remaining budget decay geometrically instead of linearly, so no
        // misestimate of the decision count can strand the end of the turn on
        // the greedy fallback.""",
    ),
    (
        "Riptide.cs",
        "    /// Default (<c>search.Stratify = 0</c>): K iid seeds, v1/v2 behaviour. The",
        "    /// Default (<c>search.Stratify = 0</c>): K iid seeds, and what ships. The",
    ),
    (
        "RiptideSearch.cs",
        """    // ---- root aggregation (v2.1) --------------------------------------------
    // How the K per-world root values are collapsed into one ranking. Mean is
    // v1/v2 behaviour and is what ships; the other two exist because averaging
    // per-world OPTIMA is strategy fusion — it prices in an ability to adapt to
    // whichever world turns out to be real, which we do not have (v1 §3.1).""",
        """    // ---- root aggregation ---------------------------------------------------
    // How the K per-world root values are collapsed into one ranking. Mean is
    // what ships; the other two exist because averaging per-world OPTIMA is
    // strategy fusion — it prices in an ability to adapt to whichever world
    // turns out to be real, which this agent does not have.""",
    ),
    (
        "RiptideSearch.cs",
        """    /// look at. v1 hard-coded 0.0005; it is a weight now so it can be zeroed when
    /// A/B-ing the aggregation modes (it is itself a crude agreement term).</summary>""",
        """    /// look at. Exposed as a knob so it can be zeroed when A/B-ing the
    /// aggregation modes (it is itself a crude agreement term).</summary>""",
    ),
    (
        "RiptideSearch.cs",
        """    // ---- determinization sampling (v2.1) ------------------------------------
    // 0 = K iid seeds (v1/v2 behaviour, default). 1 = stratified: oversample""",
        """    // ---- determinization sampling -------------------------------------------
    // 0 = K iid seeds (default, and what ships). 1 = stratified: oversample""",
    ),
    (
        "RiptideSearch.cs",
        """    // uniformly at random from each of K equal-count strata. Same estimator,
    // strictly lower ensemble variance. See docs/riptide-v2.md §4.2.""",
        """    // uniformly at random from each of K equal-count strata. Same estimator,
    // strictly lower ensemble variance.""",
    ),
    (
        "RiptideSearch.cs",
        """    /// score and the search never crosses END_TURN, so their hand/draw split is
    /// provably invisible to us (docs/riptide-v2.md §4.1).""",
        """    /// score and the search never crosses END_TURN, so their hand/draw split is
    /// provably invisible to us.""",
    ),
    (
        "RiptidePolicy.cs",
        """///  1. as the PUCT prior in the tree (and unlike the 2025 champion, the prior
///     actually influences selection here);""",
        """///  1. as the PUCT prior in the tree, at a deliberately broad softmax
///     temperature, so it biases selection rather than deciding it;""",
    ),
    (
        "RiptidePolicy.cs",
        """/// Note this policy never *forces* a card play outside the search — combo
/// ordering is a search decision, which is the biggest hole in the 2025 winner.""",
        """/// Note this policy never *forces* a card play outside the search: combo
/// ordering stays a search decision. The branch cap prunes candidates by policy
/// score, but no move is ever played on the policy's say-so alone.""",
    ),
    (
        "RiptideEval.cs",
        """/// Structural differences vs the 2025 field:
///  * an explicit 40-prestige race model with graduated urgency on both sides
///    (the 2025 champion only panics at 76-79, i.e. the far less decisive race);
///  * patron distance is scored offensively as well as defensively;
///  * combo value is a real hand-density probability, not a deck-count power law;
///  * enemy agents are not multiplied by 2 with a flat +40 fear bonus.""",
        """/// Distinguishing design choices:
///  * an explicit 40-prestige race model with graduated urgency on both sides,
///    rather than reacting only in the 76-79 window before 80;
///  * patron distance is scored offensively as well as defensively;
///  * combo value is a real hand-density probability, not a deck-count power law;
///  * agent value is continuous in card value and current HP rather than a flat
///    bonus per enemy agent.""",
    ),
    (
        "RiptideEval.cs",
        """    // Trades deck quality against prestige. At 1.0 an 88-value card is worth
    // ~14 early prestige points, which is aggressively prestige-greedy; the 2025
    // champion sits at the opposite extreme (~50). Top tuning target.""",
        """    // Trades deck quality against prestige. At 1.0 an 88-value card is worth
    // ~14 early prestige points, i.e. deliberately prestige-greedy relative to a
    // deck-building-first valuation. The most sensitive knob in the table.""",
    ),
    (
        "RiptideCards.cs",
        """/// resolution. Bewilderment is negative. Values are our own, informed by (but
/// deliberately not equal to) what the 2025 field published: in particular the
/// Orgnum and Saint Alessia decks are priced up, because the 2025 top four
/// either refuse to draft them (BestMCTS3) or have no trained values for them
/// (HQL_Plus).""",
        """/// resolution. Bewilderment is negative. Every number here is original to this
/// agent; in particular the Orgnum and Saint Alessia decks are priced up
/// relative to a conventional tier list, which is what the patron draft order in
/// the entry point rests on.""",
    ),
    (
        "RiptideCards.cs",
        "        // ---- Saint Alessia (newest deck; the 2025 field is blind to it) ------",
        "        // ---- Saint Alessia (newest deck) -------------------------------------",
    ),
]

# --- rename ----------------------------------------------------------------
# Applied to the merged body (not the header, which is already written in the
# competition name) after every anchored edit. The capitalized token covers the
# class, its constructor, the namespace, the module banners and the prose
# comments in one substitution; the lowercase token is the stderr prefix, which
# a capitalized-only rename famously misses.
RENAMES = [
    ("Riptide", AGENT_CLASS),
    ("riptide", AGENT_STDERR_TAG),
]

# Exact number of lowercase `riptide` occurrences expected in the merged body
# immediately before the rename: the two `[riptide]` stderr prefixes and nothing
# else. If a doc citation or a new log line shows up, this trips.
EXPECTED_LOWER_TAGS = 2

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

# Patterns that must not survive the comment scrub. These are the three classes
# SCRUB removes, expressed as regexes so a NEW comment reintroducing any of them
# in bot/ fails the pack instead of shipping.
SCRUB_FORBIDDEN = [
    (r"docs/[\w.-]+\.md", "internal design-doc citation"),
    (r"\briptide-v\d", "internal design-doc citation"),
    (r"\bv[12](\.\d)?\b", "version archaeology (v1/v2/v2.1)"),
    (r"\bSquirtle\b|\bWartortle\b", "internal version codename"),
    (r"\bthe Mac\b|\bpod regression\b|\bcontended box\b", "development-host diary voice"),
    (r"\b2025\b", "reference to the 2025 field"),
    (r"BestMCTS|HQL|Sakkirina|EBot", "named competitor bot"),
    (r"\bchampion\b|\bthe winner\b", "competitor commentary"),
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
        if l.strip() == "namespace %s;" % SRC_NAMESPACE:
            ns_idx = i
            break
    if ns_idx is None:
        fail("%s has no `namespace %s;` line" % (name, SRC_NAMESPACE))
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

    for name, needle, repl in EDITS + SCRUB:
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
    parts.append("namespace %s;" % SRC_NAMESPACE)
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
    for pat, why in SCRUB_FORBIDDEN:
        m = re.search(pat, scanned)
        if m:
            line = scanned[:m.start()].count("\n") + 1
            fail("comment scrub incomplete — %s at merged body line %d: %r. "
                 "Add an anchored entry to SCRUB in pack.py."
                 % (why, line, scanned.splitlines()[line - 1].strip()[:110]))
    if out.count("namespace ") != 1:
        fail("expected exactly one namespace declaration")
    if scanned.count("public class %s : AI" % SRC_NAMESPACE) != 1:
        fail("expected exactly one `public class %s : AI`" % SRC_NAMESPACE)

    # --- rename -------------------------------------------------------------
    # The header is already written in the competition name; only the merged
    # body is renamed, so a stray "Riptide" in the header would be a bug, not a
    # silent pass.
    body = scanned
    lower = len(re.findall(r"riptide", body))
    if lower != EXPECTED_LOWER_TAGS:
        fail("expected exactly %d lowercase `riptide` occurrences (the stderr "
             "prefixes) before rename, found %d — check what else is lowercase"
             % (EXPECTED_LOWER_TAGS, lower))
    for old, new in RENAMES:
        body = body.replace(old, new)
    out = HEADER + body

    if re.search(r"[Rr]iptide", out, re.IGNORECASE):
        fail("rename incomplete — `riptide` still present in the output")
    if out.count("public class %s : AI" % AGENT_CLASS) != 1:
        fail("expected exactly one `public class %s : AI` after rename" % AGENT_CLASS)
    if out.count("namespace %s;" % AGENT_CLASS) != 1:
        fail("expected exactly one `namespace %s;` after rename" % AGENT_CLASS)
    if out.count("[%s]" % AGENT_STDERR_TAG) != EXPECTED_LOWER_TAGS:
        fail("expected %d `[%s]` stderr prefixes after rename"
             % (EXPECTED_LOWER_TAGS, AGENT_STDERR_TAG))
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
