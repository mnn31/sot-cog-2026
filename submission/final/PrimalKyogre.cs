// ---------------------------------------------------------------------------
// Primal Kyogre — IEEE CoG 2026 Tales of Tribute AI Competition entry.
//
// Author: Manan Gupta (The Harker School).
//
// Single-file submission build. The six modules it merges are, in emission
// order:
//
//     PrimalKyogre.cs         AI entry point, patron draft, turn clock
//     PrimalKyogreSearch.cs   determinized-ensemble turn-plan search (PlanNode, Search)
//     PrimalKyogrePolicy.cs   move-ordering priors / rollout policy (PolicyContext, Policy)
//     PrimalKyogreEval.cs     leaf evaluation (Eval)
//     PrimalKyogreCards.cs    card valuation tables (CardValues)
//     PrimalKyogreWeights.cs  knob table (Weights)
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


using System;
using System.Collections.Generic;
using System.Diagnostics;

using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Serializers;

namespace PrimalKyogre;

// ==========================================================================
// from PrimalKyogre.cs
// ==========================================================================

/// <summary>
/// PrimalKyogre — IEEE CoG 2026 Tales of Tribute AI Competition entry.
///
/// Design in one paragraph: deliberate patron draft; a determinized ensemble
/// (K worlds, common random numbers) of PUCT best-first searches with max backup
/// over complete turn plans; our own continuous CardId-indexed evaluation with a
/// real combo-density term, an explicit 40-prestige race model, and two-sided
/// patron pressure; and a conservative cumulative turn clock with a greedy
/// policy escape hatch.
/// </summary>
public class PrimalKyogre : AI
{
    // ---- patron draft -------------------------------------------------------
    // 2026 pool: PELIN, DUKE_OF_CROWS, ANSEI, RAJHIN, ORGNUM, SAINT_ALESSIA.
    // Orgnum first: its deck is cheap, prestige-dense and combos with itself,
    // and it is the patron this evaluation prices highest at the draft stage.
    // Saint Alessia second: it is the newest deck, and the one our card values
    // price furthest from a conventional tier list, so drafting it is where the
    // valuation has the most room to pay off.
    // Out-of-pool patrons are listed so the draft stays legal if the pool changes.
    public static readonly PatronId[] PatronPreference =
    {
        PatronId.ORGNUM,
        PatronId.SAINT_ALESSIA,
        PatronId.PELIN,
        PatronId.HLAALU,
        PatronId.RAJHIN,
        PatronId.RED_EAGLE,
        PatronId.ANSEI,
        PatronId.PSIJIC,
        PatronId.DUKE_OF_CROWS,
    };

    // ---- time management ----------------------------------------------------
    // The 10 s is CUMULATIVE over every Play() call in one turn and an overrun is
    // an instant loss, so we self-impose a ceiling well below it and measure ALL
    // overhead, not just search time.
    //
    // The per-decision slice is HARDWARE-ADAPTIVE.
    //
    // A flat slice sized on one machine has two failure modes on a slower or
    // contended one: the search stops long before it has solved the reachable
    // turn plan space, AND the turn's budget is left partly unspent, because
    // nothing hands the unused time of a fast-solving decision to a slow one.
    // Both cost measurable win rate under load — enough to be worth deriving
    // the slice from measured throughput rather than fixing it in advance.
    //
    // The slice is now `SliceBoost x (what is actually left) / (decisions this
    // turn is still expected to contain)`, with two measured floors and four
    // independent ceilings:
    //   (1) decisions-per-turn, EWMA across turns and deliberately
    //       over-estimated — under-estimating starves the end of the turn,
    //       where the buys and patron calls live;
    //   (2) `left` is recomputed from the engine's own clock every call, so a
    //       decision that solved in 40 ms hands its unused share straight to the
    //       decisions after it, which a fixed per-decision cap cannot do;
    //   (3) floor A: the measured cost of an actual SOLVE on this machine
    //       (converged decisions only), so on slow hardware we spend what a
    //       solve costs instead of stopping just short of one;
    //   (4) floor B: the live iteration rate, so a slice always affords several
    //       full root sweeps per determinization — below that the root aggregate
    //       is single-world noise and worse than the greedy policy;
    //   (5) a fixed-fraction cap on the slice, so
    //       the remaining budget decays geometrically and no misestimate of the
    //       decision count can strand the turn's tail on the greedy fallback;
    //   (6) a learned overrun guard subtracted from the ceiling itself.
    //
    // This is only safe because the search is already convergence-aware: it
    // returns the instant every determinization's plan space is solved
    // (Search.Converged), so a generous slice costs nothing when there is
    // nothing left to find. "Search longer when unsolved, stop dead when
    // solved" falls out of the two mechanisms together.
    public const double TurnCeilingSeconds = 9.3;
    public const double TurnSafetyMargin = 0.70;   // subtracted from engine-reported remaining
    public const double PanicThresholdSeconds = 0.30;

    // Slice shape. A decision gets SliceBoost x the even share of whatever is
    // actually left. 1.5 is the largest value that still covers 11-12 searched
    // decisions in a turn without the tail falling into the greedy fallback
    // (worked through against the measured 7-12 decisions/turn distribution).
    public const double MinSliceSeconds = 0.12;
    public const double MaxSliceSeconds = 2.50;    // absolute cap on one decision
    public static readonly double SliceBoost = Weights.G("time.SliceBoost", 1.50);
    // Capping a slice at a FIXED FRACTION of what is left makes the remaining
    // budget decay geometrically, so it can never reach zero and the turn can
    // never run out of decisions no matter how badly the decision count was
    // estimated. Measured at six times oversubscription: without this cap the
    // agent spends 9.03 s of a 9.25 s budget and drops up to 12 tail decisions
    // into the greedy fallback; with it, none are dropped. It only binds late in
    // a turn, where the share term has already grown past it.
    public const double MaxSliceFraction = 0.25;

    // Decisions-per-turn estimate. Measured: 7-12 searched decisions per turn,
    // mean ~9.5.
    public const double DefaultDecisionsPerTurn = 9.0;
    public const double DecisionsEwmaAlpha = 0.25;
    public const double DecisionCountSafety = 1.15;   // over-estimate the tail of the turn
    public const double DecisionCountPad = 0.50;

    // What it costs THIS machine to solve a decision outright, measured only over
    // decisions that actually converged (so it is never contaminated by a slice
    // we cut short ourselves). Raises the slice floor on slow hardware: if a
    // solve here costs 1.5 s, handing a decision 0.9 s reliably buys a truncated
    // search, and a truncated search is measurably weaker than a solved one.
    public const double DefaultSolveSeconds = 0.25;
    public const double SolveEwmaAlpha = 0.30;
    public const double SolveFloorFactor = 1.00;
    public const double SolveFloorFraction = 0.25;    // ... but never > 25% of what is left

    // Search throughput on this machine. ~37 k iterations/s on the development
    // machine; a contended or slower box runs a fraction of that, and the sweep
    // floor below grows in exact proportion.
    public const double DefaultIterationsPerSecond = 35000.0;
    public const double RateEwmaAlpha = 0.25;
    public const double MinIterationsPerSecond = 200.0;
    public const double SweepsPerWorld = 4.0;         // root sweeps a slice must afford

    // Learned overrun guard: how far past its own deadline the search actually
    // runs here (clock-check granularity plus one in-flight ApplyMove, which on
    // a stalled machine is the thing that would eat the 0.70 s margin).
    public const double MinTimeGuard = 0.05;
    public const double MaxTimeGuard = 1.20;
    public const double GuardMultiplier = 2.50;
    public const double GuardDecay = 0.97;

    private readonly Stopwatch _callClock = new Stopwatch();
    private double _turnUsedSeconds;
    // Sentinel below zero so that the first Play() of every turn is detected as a
    // turn start (remainingTime jumps back up when the engine resets the clock).
    private double _lastRemainingSeconds = -1.0;
    private double _turnBudgetSeconds = TurnCeilingSeconds;

    // Hardware/opponent calibration. These describe the machine and the shape of
    // a turn, not the game, so Reset() deliberately keeps the safety-relevant
    // ones (see Reset).
    private double _decisionsPerTurn = DefaultDecisionsPerTurn;
    private double _solveSeconds = DefaultSolveSeconds;
    private double _iterationsPerSecond = DefaultIterationsPerSecond;
    private double _timeGuard = MinTimeGuard;

    private ulong _rngState;
    private ulong[] _seeds = new ulong[Search.Determinizations];
    private PlayerEnum _me = PlayerEnum.NO_PLAYER_SELECTED;
    private bool _newGame = true;
    private int _errorCount;
    private int _panicCount;

    // Switched off for the submission. In the development tree this is bound
    // to an environment variable and prints one per-turn budget/throughput line
    // to stderr; here it is a false constant folded away by the JIT, so the
    // agent emits no diagnostic output at all and there is no way to turn it on
    // without a recompile. (`static readonly`, not `const`, only so the block
    // below does not trip an unreachable-code warning.)
    private static readonly bool Diag = false;
    private int _turnDecisions;
    private int _turnIterations;
    private int _turnNodes;
    private int _turnUnconverged;
    private double _turnMaxOvershoot;
    // How often the selected aggregation rule overrode plain-mean aggregation,
    // and what the stratified seed draw cost. Both diagnostic-only.
    private int _turnAggChanged;
    private double _turnSeedSeconds;

    public PrimalKyogre()
    {
        // Force Eval/CardValues to run their static constructors once, here,
        // rather than inside the first timed decision of the first game.
        Weights.EnsureLoaded();
        Reset();
    }

    public override void PregamePrepare()
    {
        Reset();
    }

    private void Reset()
    {
        _turnUsedSeconds = 0.0;
        _lastRemainingSeconds = -1.0;
        _turnBudgetSeconds = TurnCeilingSeconds;
        _me = PlayerEnum.NO_PLAYER_SELECTED;
        _newGame = true;
        _errorCount = 0;
        _panicCount = 0;
        _turnDecisions = 0;
        _turnIterations = 0;
        _turnNodes = 0;
        _turnUnconverged = 0;
        _turnMaxOvershoot = 0.0;
        _turnAggChanged = 0;
        _turnSeedSeconds = 0.0;
        // Deliberately NOT reset: _iterationsPerSecond, _solveSeconds,
        // _decisionsPerTurn, _timeGuard. Those measure the machine we are running
        // on, which does not change between games in a run, and _timeGuard in
        // particular is a safety margin — discarding it at every GameEnd would
        // re-learn the same lesson from scratch in every game, on the one clock
        // where being wrong once loses the match. All four are clamped into a
        // safe range at the point of use and none of them enters the ceiling
        // computation except _timeGuard (which only ever tightens it), so a
        // stale value can cost search quality but cannot cost a timeout.
        // Per-game varying seed: the AI instance is reused across every game in a
        // run, so a fixed constant would make us fully reproducible (and
        // exploitable) across the whole tournament.
        unchecked
        {
            _rngState = (ulong)DateTime.UtcNow.Ticks
                        ^ ((ulong)(uint)Environment.TickCount << 21)
                        ^ ((ulong)(uint)Guid.NewGuid().GetHashCode() << 40)
                        ^ 0x9E3779B97F4A7C15UL;
            if (_rngState == 0) _rngState = 0x2545F4914F6CDD1DUL;
        }
    }

    private ulong NextRandom()
    {
        // xorshift64*
        unchecked
        {
            ulong x = _rngState;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _rngState = x;
            return x * 0x2545F4914F6CDD1DUL;
        }
    }

    // ---- patron selection ---------------------------------------------------

    public override PatronId SelectPatron(List<PatronId> availablePatrons, int round)
    {
        for (int i = 0; i < PatronPreference.Length; i++)
        {
            if (availablePatrons.Contains(PatronPreference[i])) return PatronPreference[i];
        }
        // Never hang and never return something illegal.
        return availablePatrons[0];
    }

    // ---- play ---------------------------------------------------------------

    public override Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
    {
        // Start the clock at the very top so that EVERYTHING we do in this call
        // (including determinization, prior computation and the fallback path)
        // is inside the measured budget.
        _callClock.Restart();

        Move chosen;
        try
        {
            chosen = Decide(gameState, possibleMoves, remainingTime);
        }
        catch (Exception e)
        {
            _errorCount++;
            Log("PrimalKyogre: recovered from " + e.GetType().Name + ": " + e.Message);
            chosen = SafeFallback(gameState, possibleMoves);
        }

        _callClock.Stop();
        _turnUsedSeconds += _callClock.Elapsed.TotalSeconds;
        _lastRemainingSeconds = remainingTime.TotalSeconds;

        if (chosen.Command == CommandEnum.END_TURN) RollTurn();
        return chosen;
    }

    /// <summary>
    /// End-of-turn bookkeeping: fold this turn's searched-decision count into the
    /// estimate the next turn's slices are sized from, then clear the turn-local
    /// counters. Called when we return END_TURN, and defensively from
    /// <see cref="Decide"/> if a new turn is detected without one (the game can
    /// end mid-turn, or the engine can hand us a fresh clock after a forced
    /// sequence).
    /// </summary>
    private void RollTurn()
    {
        if (Diag)
        {
            Console.Error.WriteLine(
                "[primal-kyogre] turn used=" + _turnUsedSeconds.ToString("F2") + "s"
                + " budget=" + _turnBudgetSeconds.ToString("F2") + "s"
                + " searched=" + _turnDecisions
                + " unconv=" + _turnUnconverged
                + " iters=" + _turnIterations
                + " nodes=" + _turnNodes
                + " rate=" + ((int)_iterationsPerSecond)
                + " solve=" + _solveSeconds.ToString("F2")
                + " dpt=" + _decisionsPerTurn.ToString("F1")
                + " guard=" + _timeGuard.ToString("F2")
                + " over=" + _turnMaxOvershoot.ToString("F3")
                + " aggchg=" + _turnAggChanged
                + " seedms=" + ((int)(_turnSeedSeconds * 1000.0))
                + " panic=" + _panicCount);
        }
        if (_turnDecisions > 0)
            _decisionsPerTurn += DecisionsEwmaAlpha * (_turnDecisions - _decisionsPerTurn);

        _turnUsedSeconds = 0.0;
        _lastRemainingSeconds = -1.0;
        _turnDecisions = 0;
        _turnIterations = 0;
        _turnNodes = 0;
        _turnUnconverged = 0;
        _turnMaxOvershoot = 0.0;
        _turnAggChanged = 0;
        _turnSeedSeconds = 0.0;
    }

    private Move Decide(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
    {
        if (_newGame)
        {
            _me = gameState.CurrentPlayer.PlayerID;
            _newGame = false;
        }
        if (_me == PlayerEnum.NO_PLAYER_SELECTED) _me = gameState.CurrentPlayer.PlayerID;

        double remainingSeconds = remainingTime.TotalSeconds;
        bool newTurn = remainingSeconds > _lastRemainingSeconds + 0.001;
        if (newTurn)
        {
            // A turn boundary we did not reach via our own END_TURN: still fold
            // the counters in, so the decisions-per-turn estimate never silently
            // stops updating.
            if (_turnDecisions > 0) RollTurn();
            _turnUsedSeconds = 0.0;
            _turnBudgetSeconds = TurnBudget(remainingSeconds);
            double seedT0 = _callClock.Elapsed.TotalSeconds;
            DrawSeeds(gameState, _turnBudgetSeconds);
            _turnSeedSeconds = _callClock.Elapsed.TotalSeconds - seedT0;
        }
        else if (_turnBudgetSeconds <= 0.0)
        {
            _turnBudgetSeconds = TurnBudget(remainingSeconds);
        }

        // ---- rule short circuits (deliberately only two) --------------------
        if (possibleMoves.Count == 1) return possibleMoves[0];

        Move? trivial = TrivialChoice(gameState, possibleMoves);
        if (trivial != null) return trivial;

        // ---- budget ---------------------------------------------------------
        double engineLimit = remainingSeconds - TurnSafetyMargin - _timeGuard;
        double selfLimit = _turnBudgetSeconds - _turnUsedSeconds;
        double left = Math.Min(engineLimit, selfLimit) - _callClock.Elapsed.TotalSeconds;

        if (left < PanicThresholdSeconds)
        {
            _panicCount++;
            return Policy.Best(possibleMoves, PolicyContext.From(gameState));
        }

        double slice = SliceFor(left, possibleMoves.Count);
        double started = _callClock.Elapsed.TotalSeconds;
        double deadline = started + slice;

        var search = new Search(_me, _callClock);
        Move picked = search.Run(gameState, possibleMoves, _seeds, deadline);

        RecordDecision(started, deadline, search);
        return picked;
    }

    /// <summary>
    /// Choose the turn's determinization seed set.
    ///
    /// Default (<c>search.Stratify = 0</c>): K iid seeds, and what ships. The
    /// seeds are drawn once per turn and reused for every decision and every root
    /// move, which is the common-random-numbers property root-move comparisons
    /// rest on — stratifying does not change that, it only changes *which* K
    /// worlds the turn is compared over.
    ///
    /// Stratified (<c>search.Stratify = 1</c>): draw
    /// <c>K * StratifyOversample</c> candidate worlds, score each by
    /// <see cref="Search.WorldDrawStat"/> (the value of the next few cards we
    /// would draw — the one hidden quantity the within-turn search is actually
    /// sensitive to), sort, split into K equal-count strata and take one world
    /// uniformly at random from each. Proper stratified sampling: unbiased for
    /// the same ensemble mean, with the between-strata component of the variance
    /// removed. Nothing here conditions on anything the engine would not let us
    /// observe — a candidate is a world the engine itself produced from a seed we
    /// picked, and the ranking key is computed only from our OWN deck.
    ///
    /// Cost is bounded twice: by <c>search.StratifyBudgetSeconds</c> and by a
    /// quarter of the turn budget, and the loop degrades to iid seeds rather than
    /// overrunning. It runs once per turn, not once per decision.
    /// </summary>
    private void DrawSeeds(GameState gameState, double left)
    {
        int k = _seeds.Length;
        int over = Search.StratifyOversample;
        if (Search.Stratify == 0 || over < 2)
        {
            for (int i = 0; i < k; i++) _seeds[i] = NextRandom() | 1UL;
            return;
        }

        int m = k * over;
        var cand = new ulong[m];
        var stat = new double[m];
        double cap = Search.StratifyBudgetSeconds;
        double quarter = 0.25 * (left > 0.0 ? left : 0.0);
        if (cap > quarter) cap = quarter;
        double cutoff = _callClock.Elapsed.TotalSeconds + cap;

        int got = 0;
        for (int i = 0; i < m; i++)
        {
            ulong s = NextRandom() | 1UL;
            cand[i] = s;
            try { stat[i] = Search.WorldDrawStat(gameState.ToSeededGameState(s)); }
            catch (Exception) { stat[i] = 0.0; }
            got++;
            if (_callClock.Elapsed.TotalSeconds > cutoff) break;
        }

        // Too few candidates to stratify meaningfully (slow machine, tight turn):
        // fall back to plain iid rather than pretending K strata exist.
        if (got < 2 * k)
        {
            for (int i = 0; i < k; i++) _seeds[i] = i < got ? cand[i] : (NextRandom() | 1UL);
            return;
        }

        Array.Sort(stat, cand, 0, got);
        for (int i = 0; i < k; i++)
        {
            int lo = (int)((long)i * got / k);
            int hi = (int)((long)(i + 1) * got / k);
            if (hi <= lo) hi = lo + 1;
            if (hi > got) hi = got;
            _seeds[i] = cand[lo + (int)(NextRandom() % (ulong)(hi - lo))];
        }
    }

    /// <summary>
    /// Self-imposed cumulative ceiling for a turn, tightened by the learned
    /// overrun guard. Both the engine-reported remaining time and the fixed
    /// ceiling apply; the tighter wins.
    /// </summary>
    private double TurnBudget(double remainingSeconds)
    {
        double budget = Math.Min(remainingSeconds - TurnSafetyMargin - _timeGuard,
                                 TurnCeilingSeconds - _timeGuard);
        return budget > 0.0 ? budget : 0.0;
    }

    /// <summary>
    /// Hardware-adaptive per-decision time slice: a boosted even share of what is
    /// actually left over the decisions this turn is still expected to contain,
    /// raised by two measured floors (solve cost, iteration rate) and bounded by
    /// three ceilings. The search returns the moment it has solved every
    /// determinization's plan space, so an over-generous slice on fast hardware
    /// is free; on slow hardware the same slice is what keeps the search running
    /// until it has something worth returning.
    /// </summary>
    private double SliceFor(double left, int moveCount)
    {
        // (1) How many searched decisions are still ahead of us, including this
        //     one. Over-estimated on purpose: too low starves the end of the
        //     turn, and the end of the turn is where the buys are.
        double estTotal = _decisionsPerTurn * DecisionCountSafety + DecisionCountPad;
        double remainingIncl = estTotal - _turnDecisions;
        if (remainingIncl < 1.0) remainingIncl = 1.0;

        // (2) Even share of what is ACTUALLY left, boosted. `left` is recomputed
        //     from the engine's own clock every call, so this is the whole
        //     redistribution mechanism: a decision that converged in 40 ms hands
        //     its unused share straight to the decisions after it, and the ones
        //     that are still unsolved are precisely the ones that collect it.
        //     A fixed per-decision cap cannot do this: it binds whether or not
        //     the rest of the turn has spare seconds sitting idle.
        double slice = ((left - PanicThresholdSeconds) / remainingIncl) * SliceBoost;
        if (slice > MaxSliceSeconds) slice = MaxSliceSeconds;

        // (3) Floor A — solve cost on this machine. Measured only over decisions
        //     that converged, so it is a property of the hardware and not of the
        //     cap we imposed. If solving costs 1.5 s here, a 0.9 s slice buys a
        //     truncated search; spend the 1.5 s instead. Bounded by a fraction of
        //     what is left so it decays with the turn and can never cliff-edge
        //     the tail into the greedy fallback.
        double solveFloor = SolveFloorFactor * _solveSeconds;
        double solveCap = SolveFloorFraction * left;
        if (solveFloor > solveCap) solveFloor = solveCap;
        if (slice < solveFloor) slice = solveFloor;

        // (4) Floor B — throughput. A slice that cannot afford a handful of full
        //     root sweeps in every determinization returns a root aggregate in
        //     which most moves carry a single-world estimate, which is worse than
        //     the greedy policy. Sized from the live iteration rate, so it grows
        //     in exact proportion to how slow the machine is.
        int branch = moveCount < Search.MaxRootBranch ? moveCount : Search.MaxRootBranch;
        double rate = _iterationsPerSecond;
        if (rate < MinIterationsPerSecond) rate = MinIterationsPerSecond;
        double sweep = SweepsPerWorld * (Search.Determinizations * (double)branch) / rate;
        if (slice < sweep) slice = sweep;
        if (slice < MinSliceSeconds) slice = MinSliceSeconds;

        // Structural tail guarantee, applied after the floors so it beats them:
        // never more than a fixed fraction of what is left. This makes the
        // remaining budget decay geometrically instead of linearly, so no
        // misestimate of the decision count can strand the end of the turn on
        // the greedy fallback.
        double fracCap = MaxSliceFraction * left;
        if (slice > fracCap) slice = fracCap;

        // Hard safety, applied last and unconditionally: leave the greedy escape
        // hatch intact no matter what any estimate says. `left` is already net of
        // the engine clock, the 9.3 s ceiling, the 0.70 s margin and the learned
        // overrun guard, so this is the fifth independent bound on the same
        // quantity.
        double hardMax = left - 0.5 * PanicThresholdSeconds;
        if (slice > hardMax) slice = hardMax;
        return slice > 0.0 ? slice : 0.0;
    }

    /// <summary>
    /// Fold one completed search into the calibration: cost of a decision,
    /// iteration throughput, and how far past its deadline the search overran.
    /// </summary>
    private void RecordDecision(double started, double deadline, Search search)
    {
        double ended = _callClock.Elapsed.TotalSeconds;
        double spent = ended - started;

        _turnDecisions++;
        _turnIterations += search.Iterations;
        _turnNodes += search.NodesCreated;
        if (!search.Converged) _turnUnconverged++;
        if (search.ChangedByAggregation) _turnAggChanged++;

        // Solve cost: converged decisions only. An unconverged decision tells us
        // nothing about how long a solve takes, only that our own deadline was
        // shorter than one — feeding it in here would create a loop where a short
        // slice justifies the next short slice. Iterations > 0 excludes the
        // trivially-converged case where trimming left a single root move.
        if (search.Converged && search.Iterations > 0 && spent > 0.0)
            _solveSeconds += SolveEwmaAlpha * (spent - _solveSeconds);

        // Only trust the rate over a slice long enough for the measurement not to
        // be dominated by tree construction and timer granularity.
        if (spent > 0.02 && search.Iterations > 0)
        {
            double rate = search.Iterations / spent;
            _iterationsPerSecond += RateEwmaAlpha * (rate - _iterationsPerSecond);
            if (_iterationsPerSecond < MinIterationsPerSecond)
                _iterationsPerSecond = MinIterationsPerSecond;
        }

        // Decayed max of the observed overrun. The search polls the clock at
        // every node and every rollout step, so this is the cost of the longest
        // uninterruptible unit of work (one ApplyMove) on this machine — exactly
        // the quantity that would eat the margin to the 10 s wall under load.
        double overshoot = ended - deadline;
        if (overshoot > _turnMaxOvershoot) _turnMaxOvershoot = overshoot;
        double guard = _timeGuard * GuardDecay;
        if (overshoot > 0.0)
        {
            double want = GuardMultiplier * overshoot;
            if (want > guard) guard = want;
        }
        if (guard < MinTimeGuard) guard = MinTimeGuard;
        if (guard > MaxTimeGuard) guard = MaxTimeGuard;
        _timeGuard = guard;
    }

    /// <summary>
    /// The only decisions we take away from the search: choices whose answer is
    /// unconditionally correct (dumping Bewilderment). Everything else — combo
    /// ordering above all — stays inside the search.
    /// </summary>
    private static Move? TrivialChoice(GameState gameState, List<Move> possibleMoves)
    {
        var choice = gameState.PendingChoice;
        if (choice == null) return null;
        if (choice.Type != Choice.DataType.CARD) return null;

        var followUp = choice.ChoiceFollowUp;
        if (followUp != ChoiceFollowUp.DESTROY_CARDS
            && followUp != ChoiceFollowUp.DISCARD_CARDS
            && followUp != ChoiceFollowUp.COMPLETE_TREASURY) return null;

        Move? best = null;
        int bestCount = 0;
        for (int i = 0; i < possibleMoves.Count; i++)
        {
            var m = possibleMoves[i] as MakeChoiceMove<UniqueCard>;
            if (m == null || m.Choices.Count == 0) continue;
            bool allBewilderment = true;
            for (int j = 0; j < m.Choices.Count; j++)
            {
                if (m.Choices[j].CommonId != CardId.BEWILDERMENT) { allBewilderment = false; break; }
            }
            if (allBewilderment && m.Choices.Count > bestCount)
            {
                bestCount = m.Choices.Count;
                best = possibleMoves[i];
            }
        }
        return best;
    }

    private Move SafeFallback(GameState gameState, List<Move> possibleMoves)
    {
        try
        {
            return Policy.Best(possibleMoves, PolicyContext.From(gameState));
        }
        catch (Exception)
        {
            for (int i = 0; i < possibleMoves.Count; i++)
                if (possibleMoves[i].Command != CommandEnum.END_TURN) return possibleMoves[i];
            return possibleMoves[0];
        }
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
        if (_errorCount > 0 || _panicCount > 0)
            Log("PrimalKyogre game end: recovered errors=" + _errorCount + " panic fallbacks=" + _panicCount);

        // Always-on self-report of the three fatal outcomes. GameRunner only
        // aggregates these as "ends due to other factors" and never says whose
        // fault it was, which makes a timing regression invisible in a batch
        // result — and a timing regression is the one bug class that costs whole
        // matches. One stderr line, only ever on a loss by our own fault.
        if ((state.Reason == GameEndReason.TURN_TIMEOUT
             || state.Reason == GameEndReason.BOT_EXCEPTION
             || state.Reason == GameEndReason.INCORRECT_MOVE
             || state.Reason == GameEndReason.PATRON_SELECTION_TIMEOUT
             || state.Reason == GameEndReason.PREPARE_TIME_EXCEEDED)
            && _me != PlayerEnum.NO_PLAYER_SELECTED
            && state.Winner != _me)
        {
            Console.Error.WriteLine("[primal-kyogre] FATAL " + state.Reason + " as " + _me
                                    + " (turnUsed=" + _turnUsedSeconds.ToString("F2")
                                    + "s guard=" + _timeGuard.ToString("F2") + "s)");
        }
        Reset();
    }
}

// ==========================================================================
// from PrimalKyogreSearch.cs
// ==========================================================================

/// <summary>
/// One node of the within-turn plan tree for a single determinization.
/// Every node belongs to us (the turn does not alternate), so max-backup is the
/// correct operator: we are solving a single-agent planning problem, not a
/// minimax game, until END_TURN is applied.
/// </summary>
internal sealed class PlanNode
{
    public SeededGameState State = null!;
    public List<Move> Moves = null!;
    public PlanNode?[] Children = null!;
    public double[] ChildValue = null!;
    public int[] ChildVisits = null!;
    public bool[] ChildSolved = null!;
    public double[] Prior = null!;
    public int Visits;
    public double Value;
    // Solved: this node's value cannot change with more search. Because every
    // transition inside a determinization is deterministic (seedless ApplyMove
    // on a fixed-seed world), a fully expanded subtree is genuinely solved and
    // re-visiting it is pure waste — so we prune it out of selection entirely.
    public bool Leaf;

    public PlanNode(SeededGameState state, List<Move> moves, PolicyContext ctx, bool leaf)
    {
        State = state;
        Moves = moves;
        Leaf = leaf;
        int n = moves.Count;
        Children = new PlanNode?[n];
        ChildValue = new double[n];
        ChildVisits = new int[n];
        ChildSolved = new bool[n];
        Prior = new double[n];
        if (!leaf) ComputePrior(ctx);
    }

    private void ComputePrior(PolicyContext ctx)
    {
        int n = Moves.Count;
        double max = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            Prior[i] = Policy.Score(Moves[i], ctx);
            if (Prior[i] > max) max = Prior[i];
        }
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            // Broad temperature: the prior should bias the search, not decide it.
            Prior[i] = Math.Exp((Prior[i] - max) / Search.PriorTemperature);
            sum += Prior[i];
        }
        if (sum <= 0) sum = 1.0;
        for (int i = 0; i < n; i++) Prior[i] /= sum;
    }
}

/// <summary>
/// Determinized ensemble search over complete turn plans.
///
/// K independent determinizations of the hidden information are searched in
/// lockstep (common random numbers: the same seed set is reused for every root
/// move, so root-move comparisons are paired). Within each determinization a
/// PUCT-guided best-first search with max backup explores the current turn's
/// move sequences, evaluating states after END_TURN or at the search horizon.
/// Root values are collapsed across determinizations by one of three selectable
/// rules (<c>search.Aggregation</c>): mean, majority vote of the per-world best
/// moves, or mean penalised by cross-world disagreement.
/// </summary>
public sealed class Search
{
    // ---- tuning knobs -------------------------------------------------------
    // Measured: with K=6 the search solves the entire turn plan space of every
    // world and exits having used only 0.3-2.2 s of the 9.3 s turn budget. The
    // spare budget is worth far more spent on additional hidden-information
    // samples than on depth we already have.
    public const int Determinizations = 14;
    public const double PriorTemperature = 25.0;
    public const double Exploration = 0.55;     // PUCT c
    public const int MaxDepth = 24;             // plies within a turn before we truncate
    public const int MaxRolloutLength = 70;
    public const int MaxNodesPerDecision = 16000;   // ~25 MB transient, freed per decision
    public const int MaxRootBranch = 22;
    public const int MaxChoiceBranch = 14;

    // ---- root aggregation ---------------------------------------------------
    // How the K per-world root values are collapsed into one ranking. Mean is
    // what ships; the other two exist because averaging per-world OPTIMA is
    // strategy fusion — it prices in an ability to adapt to whichever world
    // turns out to be real, which this agent does not have.
    //
    // Fixed at pack time in the submission build (the development tree binds it
    // from the tuning harness instead, so an A/B costs no rebuild):
    //   search.Aggregation = 0  mean over worlds                       [SHIPPED]
    //                        1  majority vote of per-world best moves, mean as
    //                           the tiebreak
    //                        2  mean - lambda * cross-world std dev
    public const int AggMean = 0;
    public const int AggVote = 1;
    public const int AggPenalty = 2;

    public static readonly int Aggregation = (int)Weights.G("search.Aggregation", AggMean);

    /// <summary>Std-dev penalty strength for <see cref="AggPenalty"/>. Values are in
    /// (0,1) eval units, where a whole turn is typically worth ~0.02-0.10, so a
    /// useful lambda is O(0.1-1).</summary>
    public static readonly double DisagreementLambda = Weights.G("search.DisagreementLambda", 1.0);

    /// <summary>Per-world bonus for a root move every determinization bothered to
    /// look at. Exposed as a knob so it can be zeroed when A/B-ing the
    /// aggregation modes (it is itself a crude agreement term).</summary>
    public static readonly double BreadthBonus = Weights.G("search.BreadthBonus", 0.0005);

    // ---- determinization sampling -------------------------------------------
    // 0 = K iid seeds (default, and what ships). 1 = stratified: oversample
    // candidate worlds, rank them by the one latent variable the within-turn
    // search is actually sensitive to (our own draw order), and take one world
    // uniformly at random from each of K equal-count strata. Same estimator,
    // strictly lower ensemble variance.
    public static readonly int Stratify = (int)Weights.G("search.Stratify", 0);
    public static readonly int StratifyOversample = (int)Weights.G("search.StratifyOversample", 4);
    /// <summary>How many upcoming cards of our own deck the stratification
    /// statistic looks at.</summary>
    public static readonly int StratifyDepth = (int)Weights.G("search.StratifyDepth", 5);
    /// <summary>Hard wall-clock cap on candidate generation, per turn.</summary>
    public static readonly double StratifyBudgetSeconds =
        Weights.G("search.StratifyBudgetSeconds", 0.15);

    /// <summary>
    /// The scalar a determinized world is stratified on: the value of the next
    /// <see cref="StratifyDepth"/> cards we would draw in that world, continuing
    /// into the (also scrambled) cooldown pile when the draw pile is short.
    ///
    /// This is the *only* hidden quantity the within-turn search is materially
    /// sensitive to. It is deliberately not a function of the opponent's hand:
    /// the leaf evaluation pools their Hand/DrawPile/CooldownPile into one deck
    /// score and the search never crosses END_TURN, so their hand/draw split is
    /// provably invisible to us.
    /// </summary>
    public static double WorldDrawStat(SeededGameState s)
    {
        var p = s.CurrentPlayer;
        int need = StratifyDepth;
        if (need < 1) need = 1;
        double v = 0.0;
        var draw = p.DrawPile;
        for (int i = 0; i < draw.Count && need > 0; i++, need--) v += CardValues.HandValue(draw[i]);
        var cd = p.CooldownPile;
        for (int i = 0; i < cd.Count && need > 0; i++, need--) v += CardValues.HandValue(cd[i]);
        return v;
    }

    private readonly PlayerEnum _me;
    private readonly Stopwatch _clock;
    private double _deadlineSeconds;
    private int _nodesCreated;
    private bool _abort;

    public int Iterations { get; private set; }
    public int NodesCreated => _nodesCreated;

    /// <summary>
    /// True when every determinization's turn plan space was solved exactly, so
    /// the search returned because there was nothing left to find rather than
    /// because it ran out of time. This is the signal the adaptive time slice is
    /// built on: converged decisions hand their unused budget back to the turn,
    /// unconverged ones are the reason a slice is worth widening on slow
    /// hardware.
    /// </summary>
    public bool Converged { get; private set; }

    /// <summary>Determinizations whose plan space was solved (diagnostics).</summary>
    public int WorldsSolved { get; private set; }

    /// <summary>
    /// True when the selected aggregation rule picked a different root move than
    /// plain mean aggregation would have. Diagnostics only, but it is the number
    /// that makes a win-rate A/B interpretable: an aggregation mode that never
    /// changes a move cannot change a result, and at n = 24 the win rate alone
    /// cannot tell those two cases apart.
    /// </summary>
    public bool ChangedByAggregation { get; private set; }

    public Search(PlayerEnum me, Stopwatch turnClock)
    {
        _me = me;
        _clock = turnClock;
    }

    /// <summary>
    /// Pick the best move from <paramref name="rootMoves"/> (which must be the
    /// engine-supplied legal move list, so the returned object is legal).
    /// </summary>
    public Move Run(GameState gameState, List<Move> rootMoves, ulong[] seeds, double deadlineSeconds)
    {
        _deadlineSeconds = deadlineSeconds;
        _nodesCreated = 0;
        _abort = false;
        Iterations = 0;
        Converged = false;
        WorldsSolved = 0;
        ChangedByAggregation = false;

        List<Move> roots = TrimRoot(rootMoves, PolicyContext.From(gameState));
        int n = roots.Count;
        if (n == 1) { Converged = true; return roots[0]; }

        int k = seeds.Length;
        var trees = new PlanNode?[k];
        var sum = new double[n];
        var sumSq = new double[n];
        var count = new int[n];
        var votes = new int[n];

        for (int i = 0; i < k; i++)
        {
            if (OutOfTime()) break;
            SeededGameState sgs = gameState.ToSeededGameState(seeds[i]);
            trees[i] = new PlanNode(sgs, roots, PolicyContext.From(sgs, _me), false);
            _nodesCreated++;
        }

        // Round-robin one iteration per determinization so the budget is split
        // evenly and every tree stays anytime-usable. A tree drops out once its
        // whole turn plan space is solved, handing its budget to the others.
        var done = new bool[k];
        bool progress = true;
        while (!OutOfTime() && progress)
        {
            progress = false;
            for (int i = 0; i < k; i++)
            {
                var t = trees[i];
                if (t == null || done[i]) continue;
                if (OutOfTime()) break;
                Iterate(t, 0);
                Iterations++;
                if (t.Leaf) { done[i] = true; continue; }
                progress = true;
            }
        }

        // Converged == every world we managed to build was solved exactly. A
        // world we never got round to building (the deadline expired during
        // determinization) counts as not converged.
        int built = 0;
        for (int i = 0; i < k; i++)
        {
            if (trees[i] == null) continue;
            built++;
            if (trees[i]!.Leaf) WorldsSolved++;
        }
        Converged = built == k && WorldsSolved == k;

        // Per-world statistics. Every world also casts one vote for its own best
        // root move — that vote is what makes AggVote a *consensus* rule rather
        // than an average of per-world optima.
        for (int i = 0; i < k; i++)
        {
            var t = trees[i];
            if (t == null) continue;
            int voteFor = -1;
            double voteVal = double.NegativeInfinity;
            for (int j = 0; j < n; j++)
            {
                if (t.ChildVisits[j] <= 0) continue;
                double v = t.ChildValue[j];
                sum[j] += v;
                sumSq[j] += v * v;
                count[j]++;
                if (v > voteVal) { voteVal = v; voteFor = j; }
            }
            if (voteFor >= 0) votes[voteFor]++;
        }

        int best = -1;
        double bestVal = double.NegativeInfinity;
        int bestVotes = -1;
        int plainBest = -1;
        double plainVal = double.NegativeInfinity;
        int mode = Aggregation;
        for (int j = 0; j < n; j++)
        {
            if (count[j] == 0) continue;
            double mean = sum[j] / count[j];
            // Prefer moves that every determinization agreed to look at.
            double v = mean + BreadthBonus * count[j];
            if (v > plainVal) { plainVal = v; plainBest = j; }

            if (mode == AggPenalty && count[j] > 1)
            {
                // Population std dev of this move's value across worlds. A move
                // that is excellent in half the worlds and terrible in the other
                // half is exactly what mean aggregation over-trusts: we cannot
                // choose which world we are in, so pay for the disagreement.
                double var2 = sumSq[j] / count[j] - mean * mean;
                if (var2 > 0.0) v -= DisagreementLambda * Math.Sqrt(var2);
            }

            if (mode == AggVote)
            {
                // Votes are primary, the mean-based score only breaks ties.
                if (votes[j] > bestVotes || (votes[j] == bestVotes && v > bestVal))
                {
                    bestVotes = votes[j];
                    bestVal = v;
                    best = j;
                }
            }
            else if (v > bestVal) { bestVal = v; best = j; }
        }

        ChangedByAggregation = best >= 0 && plainBest >= 0 && best != plainBest;

        if (best < 0) return Policy.Best(rootMoves, PolicyContext.From(gameState));
        return roots[best];
    }

    // ---- tree ---------------------------------------------------------------

    private double Iterate(PlanNode node, int depth)
    {
        if (node.Leaf) return node.Value;
        if (_abort || OutOfTime()) return node.Value;

        int n = node.Moves.Count;
        int sel = -1;
        double bestScore = double.NegativeInfinity;
        double sqrtN = Math.Sqrt(node.Visits + 1.0);
        // First-play urgency: unvisited children inherit the parent's value.
        double fpu = node.Visits > 0 ? node.Value : 0.5;

        // At the root, sweep every candidate once before deepening: the ensemble
        // aggregate averages per-move values across determinizations, so a move
        // that only one world ever looked at would carry a biased estimate.
        if (depth == 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (node.ChildVisits[i] != 0 || node.ChildSolved[i]) continue;
                if (node.Prior[i] > bestScore) { bestScore = node.Prior[i]; sel = i; }
            }
            if (sel >= 0) goto selected;
            bestScore = double.NegativeInfinity;
        }

        for (int i = 0; i < n; i++)
        {
            if (node.ChildSolved[i]) continue;
            double q = node.ChildVisits[i] > 0 ? node.ChildValue[i] : fpu;
            double u = Exploration * node.Prior[i] * sqrtN / (1.0 + node.ChildVisits[i]);
            double score = q + u;
            if (score > bestScore) { bestScore = score; sel = i; }
        }

        selected:

        if (sel < 0)
        {
            // Every continuation from here is solved: so is this node.
            node.Leaf = true;
            return node.Value;
        }

        double value;
        Move move = node.Moves[sel];
        bool solved = false;

        if (move.Command == CommandEnum.END_TURN)
        {
            value = Rollout(node.State, move);
            solved = true;
        }
        else if (depth + 1 >= MaxDepth || _nodesCreated >= MaxNodesPerDecision)
        {
            value = Rollout(node.State, move);
            solved = true;   // we will not refine this line further
        }
        else if (node.ChildVisits[sel] == 0)
        {
            value = Rollout(node.State, move);
        }
        else
        {
            PlanNode? child = node.Children[sel];
            if (child == null)
            {
                child = Expand(node.State, move);
                if (child == null)
                {
                    // Simulation failed for this move: never take it again.
                    node.ChildValue[sel] = 0.0;
                    node.ChildSolved[sel] = true;
                    node.ChildVisits[sel]++;
                    node.Visits++;
                    return node.Value;
                }
                node.Children[sel] = child;
            }
            value = Iterate(child, depth + 1);
            solved = child.Leaf;
        }

        if (value > node.ChildValue[sel] || node.ChildVisits[sel] == 0) node.ChildValue[sel] = value;
        node.ChildVisits[sel]++;
        if (solved) node.ChildSolved[sel] = true;
        node.Visits++;

        double max = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
            if (node.ChildVisits[i] > 0 && node.ChildValue[i] > max) max = node.ChildValue[i];
        if (max > double.NegativeInfinity) node.Value = max;

        return node.Value;
    }

    private PlanNode? Expand(SeededGameState state, Move move)
    {
        try
        {
            var (next, moves) = state.ApplyMove(move);
            _nodesCreated++;
            bool leaf = move.Command == CommandEnum.END_TURN || next.GameEndState != null || moves.Count == 0;
            if (leaf)
            {
                var lf = new PlanNode(next, moves, null!, true);
                lf.Value = Eval.Evaluate(next, _me);
                return lf;
            }
            var ctx = PolicyContext.From(next, _me);
            var trimmed = TrimBranch(moves, ctx);
            return new PlanNode(next, trimmed, ctx, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Apply <paramref name="move"/> then play the rest of the turn greedily under
    /// the policy and evaluate the resulting end-of-turn state.
    /// </summary>
    private double Rollout(SeededGameState state, Move move)
    {
        try
        {
            var (s, moves) = state.ApplyMove(move);
            if (move.Command == CommandEnum.END_TURN || s.GameEndState != null)
                return Eval.Evaluate(s, _me);

            for (int step = 0; step < MaxRolloutLength; step++)
            {
                if (moves.Count == 0) break;
                if (OutOfTime()) { _abort = true; break; }

                var ctx = PolicyContext.From(s, _me);
                Move next = Policy.Best(moves, ctx);
                var (s2, moves2) = s.ApplyMove(next);
                s = s2;
                moves = moves2;
                if (next.Command == CommandEnum.END_TURN || s.GameEndState != null) break;
            }
            return Eval.Evaluate(s, _me);
        }
        catch (Exception)
        {
            return 0.0;
        }
    }

    // ---- branching control --------------------------------------------------

    private List<Move> TrimRoot(List<Move> moves, PolicyContext ctx)
    {
        return Trim(moves, ctx, MaxRootBranch);
    }

    private List<Move> TrimBranch(List<Move> moves, PolicyContext ctx)
    {
        int cap = ctx.BoardState == BoardState.NORMAL ? MaxRootBranch : MaxChoiceBranch;
        return Trim(moves, ctx, cap);
    }

    /// <summary>
    /// Keep the top <paramref name="cap"/> moves by policy score. END_TURN is
    /// always retained. Nothing is force-played: this only caps the combinatorial
    /// MAKE_CHOICE explosion and very wide buy phases.
    /// </summary>
    private static List<Move> Trim(List<Move> moves, PolicyContext ctx, int cap)
    {
        if (moves.Count <= cap) return moves;

        int n = moves.Count;
        var scores = new double[n];
        var idx = new int[n];
        Move? endTurn = null;
        for (int i = 0; i < n; i++)
        {
            scores[i] = Policy.Score(moves[i], ctx);
            idx[i] = i;
            if (moves[i].Command == CommandEnum.END_TURN) endTurn = moves[i];
        }
        Array.Sort(scores, idx);
        var kept = new List<Move>(cap + 1);
        for (int i = n - 1; i >= 0 && kept.Count < cap; i--) kept.Add(moves[idx[i]]);
        if (endTurn != null && !kept.Contains(endTurn)) kept.Add(endTurn);
        return kept;
    }

    private bool OutOfTime()
    {
        return _clock.Elapsed.TotalSeconds >= _deadlineSeconds;
    }
}

// ==========================================================================
// from PrimalKyogrePolicy.cs
// ==========================================================================

/// <summary>
/// A lightweight, perspective-fixed view of whatever board we are scoring moves
/// against. Built either from the real <see cref="GameState"/> (for the greedy
/// panic fallback, which must not pay for a determinization) or from a
/// <see cref="SeededGameState"/> inside the search.
/// </summary>
public sealed class PolicyContext
{
    public List<UniqueCard> Hand = new List<UniqueCard>();
    public List<SerializedAgent> MyAgents = new List<SerializedAgent>();
    public List<SerializedAgent> OppAgents = new List<SerializedAgent>();
    public int Coins;
    public int Power;
    public int Prestige;
    public int OppPrestige;
    public uint PatronCalls;
    public int MyCardCount;
    public BoardState BoardState;
    public SerializedChoice? PendingChoice;
    public PatronStates? Patrons;
    public PlayerEnum Me;
    public readonly int[] HandDeckCount = new int[16];
    public readonly int[] MyDeckCount = new int[16];

    public static PolicyContext From(SeededGameState s, PlayerEnum me)
    {
        bool myTurn = s.CurrentPlayer.PlayerID == me;
        SerializedPlayer mine = myTurn ? s.CurrentPlayer : s.EnemyPlayer;
        SerializedPlayer theirs = myTurn ? s.EnemyPlayer : s.CurrentPlayer;
        var ctx = new PolicyContext
        {
            Hand = mine.Hand,
            MyAgents = mine.Agents,
            OppAgents = theirs.Agents,
            Coins = mine.Coins,
            Power = mine.Power,
            Prestige = mine.Prestige,
            OppPrestige = theirs.Prestige,
            PatronCalls = mine.PatronCalls,
            BoardState = s.BoardState,
            PendingChoice = s.PendingChoice,
            Patrons = s.PatronStates,
            Me = me,
        };
        ctx.Count(mine.Hand, ctx.HandDeckCount);
        ctx.MyCardCount = mine.Hand.Count + mine.DrawPile.Count + mine.CooldownPile.Count
                          + mine.Played.Count + mine.Agents.Count;
        ctx.Count(mine.Hand, ctx.MyDeckCount);
        ctx.Count(mine.DrawPile, ctx.MyDeckCount);
        ctx.Count(mine.CooldownPile, ctx.MyDeckCount);
        ctx.Count(mine.Played, ctx.MyDeckCount);
        return ctx;
    }

    public static PolicyContext From(GameState s)
    {
        var mine = s.CurrentPlayer;
        var theirs = s.EnemyPlayer;
        var ctx = new PolicyContext
        {
            Hand = mine.Hand,
            MyAgents = mine.Agents,
            OppAgents = theirs.Agents,
            Coins = mine.Coins,
            Power = mine.Power,
            Prestige = mine.Prestige,
            OppPrestige = theirs.Prestige,
            PatronCalls = mine.PatronCalls,
            BoardState = s.BoardState,
            PendingChoice = s.PendingChoice,
            Patrons = s.PatronStates,
            Me = mine.PlayerID,
        };
        ctx.Count(mine.Hand, ctx.HandDeckCount);
        ctx.MyCardCount = mine.Hand.Count + mine.DrawPile.Count + mine.CooldownPile.Count
                          + mine.Played.Count + mine.Agents.Count;
        ctx.Count(mine.Hand, ctx.MyDeckCount);
        ctx.Count(mine.DrawPile, ctx.MyDeckCount);
        ctx.Count(mine.CooldownPile, ctx.MyDeckCount);
        ctx.Count(mine.Played, ctx.MyDeckCount);
        return ctx;
    }

    private void Count(List<UniqueCard> cards, int[] into)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            int d = (int)cards[i].Deck;
            if ((uint)d < 16) into[d]++;
        }
    }
}

/// <summary>
/// Hand-written move-ordering logits. These are used three ways:
///  1. as the PUCT prior in the tree, at a deliberately broad softmax
///     temperature, so it biases selection rather than deciding it;
///  2. as the rollout policy (greedy argmax) to end of turn;
///  3. as the panic fallback when the turn clock is nearly exhausted.
///
/// Note this policy never *forces* a card play outside the search: combo
/// ordering stays a search decision. The branch cap prunes candidates by policy
/// score, but no move is ever played on the policy's say-so alone.
/// </summary>
public static class Policy
{
    public static double Score(Move m, PolicyContext c)
    {
        switch (m.Command)
        {
            case CommandEnum.END_TURN:
                return -400.0;

            case CommandEnum.PLAY_CARD:
            {
                var card = ((SimpleCardMove)m).Card;
                double s = 60.0;
                // Deck concentration: playing from the deck we hold most of
                // advances the combo counter that pays off best.
                int d = (int)card.Deck;
                if ((uint)d < 16 && card.Deck != PatronId.TREASURY)
                    s += 5.0 * Math.Min(4, c.HandDeckCount[d]) * CardValues.Combo(card.Deck);
                // Cheap resource cards go early so later decisions see the coin.
                if (card.Cost == 0) s += 6.0;
                if (card.CommonId == CardId.BEWILDERMENT) s -= 40.0;
                if (card.Type == CardType.AGENT) s += 4.0;
                return s;
            }

            case CommandEnum.ACTIVATE_AGENT:
            {
                var card = ((SimpleCardMove)m).Card;
                return 40.0 + CardValues.DeckValue(card) * 0.05;
            }

            case CommandEnum.ATTACK:
            {
                var card = ((SimpleCardMove)m).Card;
                double s = 24.0 + CardValues.DeckValue(card) * 0.10;
                if (card.Taunt) s += 12.0;
                return s;
            }

            case CommandEnum.BUY_CARD:
            {
                var card = ((SimpleCardMove)m).Card;
                double s = -25.0 + CardValues.DeckValue(card) * 0.35;
                int d = (int)card.Deck;
                if ((uint)d < 16 && card.Deck != PatronId.TREASURY)
                    s += 1.4 * Math.Min(8, c.MyDeckCount[d]);
                if (card.Type == CardType.CONTRACT_AGENT) s += 6.0;
                return s;
            }

            case CommandEnum.CALL_PATRON:
                return PatronScore(((SimplePatronMove)m).PatronId, c);

            case CommandEnum.MAKE_CHOICE:
                return ChoiceScore(m, c);
        }
        return 0.0;
    }

    private static double PatronScore(PatronId p, PolicyContext c)
    {
        double s = -60.0;
        PlayerEnum owner = PlayerEnum.NO_PLAYER_SELECTED;
        if (c.Patrons != null) c.Patrons.All.TryGetValue(p, out owner);
        bool mine = owner == c.Me;
        double progress = Math.Min(1.0, Math.Max(c.Prestige, c.OppPrestige) / 40.0);

        switch (p)
        {
            case PatronId.ORGNUM:
                // Scales with total cards owned; strong once the deck is fat.
                s += 22.0 + (mine ? c.MyCardCount * 0.55 : c.MyCardCount * 0.30);
                break;
            case PatronId.SAINT_ALESSIA:
                s += mine ? 20.0 : 12.0;
                break;
            case PatronId.DUKE_OF_CROWS:
                // Convert coin to power, but only when the bank is big; holding
                // the favour locks us out of ever calling it again.
                s += (c.Coins - 6.0) * 4.0 + progress * 22.0;
                break;
            case PatronId.ANSEI:
                s += 16.0 - 8.0 * progress;
                break;
            case PatronId.PELIN:
                s += 12.0;
                break;
            case PatronId.RAJHIN:
                s += 10.0 - 4.0 * progress;
                break;
            case PatronId.TREASURY:
                s += 6.0 - 4.0 * progress;
                break;
            default:
                s += 6.0;
                break;
        }

        if (!mine) s += 14.0; // taking a favour is worth more than re-taking one
        return s;
    }

    private static double ChoiceScore(Move m, PolicyContext c)
    {
        var effectMove = m as MakeChoiceMove<UniqueEffect>;
        if (effectMove != null)
        {
            double es = 0.0;
            for (int i = 0; i < effectMove.Choices.Count; i++) es += EffectScore(effectMove.Choices[i], c);
            return es;
        }

        var choice = c.PendingChoice;
        var cardMove = m as MakeChoiceMove<UniqueCard>;
        if (choice == null || cardMove == null) return 0.0;

        var cards = cardMove.Choices;
        double s = 0.0;
        switch (choice.ChoiceFollowUp)
        {
            case ChoiceFollowUp.DESTROY_CARDS:
            case ChoiceFollowUp.COMPLETE_TREASURY:
                // Getting rid of the worst card is what we want.
                for (int i = 0; i < cards.Count; i++)
                {
                    double v = CardValues.DeckValue(cards[i]);
                    s -= v;
                    if (cards[i].CommonId == CardId.BEWILDERMENT) s += 200.0;
                    else if (cards[i].CommonId == CardId.GOLD) s += 40.0;
                }
                return s + 10.0 * cards.Count;

            case ChoiceFollowUp.DISCARD_CARDS:
                for (int i = 0; i < cards.Count; i++)
                {
                    s -= CardValues.HandValue(cards[i]);
                    if (cards[i].CommonId == CardId.BEWILDERMENT) s += 120.0;
                }
                return s - 6.0 * cards.Count;

            case ChoiceFollowUp.ACQUIRE_CARDS:
                for (int i = 0; i < cards.Count; i++) s += CardValues.DeckValue(cards[i]);
                return s;

            case ChoiceFollowUp.REFRESH_CARDS:
            case ChoiceFollowUp.COMPLETE_PELLIN:
                for (int i = 0; i < cards.Count; i++) s += CardValues.HandValue(cards[i]) * 0.6;
                return s;

            case ChoiceFollowUp.TOSS_CARDS:
                for (int i = 0; i < cards.Count; i++) s -= CardValues.HandValue(cards[i]) * 0.5;
                return s;

            case ChoiceFollowUp.KNOCKOUT_AGENTS:
                for (int i = 0; i < cards.Count; i++)
                {
                    // The choice list can contain our own agents too; killing an
                    // enemy agent is what we want.
                    bool ours = false;
                    for (int j = 0; j < c.MyAgents.Count; j++)
                        if (c.MyAgents[j].RepresentingCard.UniqueId == cards[i].UniqueId) { ours = true; break; }
                    s += ours ? -CardValues.DeckValue(cards[i]) : CardValues.DeckValue(cards[i]);
                }
                return s;

            case ChoiceFollowUp.REPLACE_CARDS_IN_TAVERN:
                for (int i = 0; i < cards.Count; i++) s -= CardValues.DeckValue(cards[i]) * 0.4;
                return s;

            case ChoiceFollowUp.DONATE:
                for (int i = 0; i < cards.Count; i++) s -= CardValues.DeckValue(cards[i]);
                return s;

            default:
                for (int i = 0; i < cards.Count; i++) s += CardValues.DeckValue(cards[i]) * 0.2;
                return s;
        }
    }

    /// <summary>Rough utility of a single "choose one of these effects" option.</summary>
    private static double EffectScore(UniqueEffect e, PolicyContext c)
    {
        double progress = Math.Min(1.0, Math.Max(c.Prestige, c.OppPrestige) / 40.0);
        switch (e.Type)
        {
            case EffectType.GAIN_COIN: return e.Amount * (4.0 - 1.5 * progress);
            case EffectType.GAIN_POWER: return e.Amount * (3.5 + 2.0 * progress);
            case EffectType.GAIN_PRESTIGE: return e.Amount * (6.0 + 4.0 * progress);
            case EffectType.OPP_LOSE_PRESTIGE: return e.Amount * (5.5 + 4.0 * progress);
            case EffectType.DRAW: return e.Amount * 6.0;
            case EffectType.DESTROY_CARD: return e.Amount * 7.0 * (1.0 - 0.5 * progress);
            case EffectType.ACQUIRE_TAVERN: return e.Amount * 3.0 * (1.0 - 0.5 * progress);
            case EffectType.KNOCKOUT: return e.Amount * 6.0;
            case EffectType.KNOCKOUT_ALL: return c.OppAgents.Count * 7.0;
            case EffectType.PATRON_CALL: return e.Amount * 9.0;
            case EffectType.HEAL: return e.Amount * 2.0;
            case EffectType.RETURN_TOP: return e.Amount * 3.0;
            case EffectType.RETURN_AGENT_TOP: return e.Amount * 3.5;
            case EffectType.CREATE_SUMMERSET_SACKING: return e.Amount * 5.0;
            case EffectType.OPP_DISCARD: return e.Amount * 5.0;
            case EffectType.TOSS: return e.Amount * 1.0;
            case EffectType.REPLACE_TAVERN: return e.Amount * 1.5;
            case EffectType.DONATE: return -e.Amount * 2.0;
            default: return 0.0;
        }
    }

    /// <summary>Greedy argmax over the policy. Never returns null for a non-empty list.</summary>
    public static Move Best(List<Move> moves, PolicyContext c)
    {
        Move best = moves[0];
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < moves.Count; i++)
        {
            double s = Score(moves[i], c);
            if (s > bestScore) { bestScore = s; best = moves[i]; }
        }
        return best;
    }
}

// ==========================================================================
// from PrimalKyogreEval.cs
// ==========================================================================

/// <summary>
/// Leaf evaluation. Returns a win-probability-like score in (0,1) from the
/// perspective of <c>me</c>. Every backup site in the search uses this same
/// scale — there is no negation, no zero-sum mixing.
///
/// Distinguishing design choices:
///  * an explicit 40-prestige race model with graduated urgency on both sides,
///    rather than reacting only in the 76-79 window before 80;
///  * patron distance is scored offensively as well as defensively;
///  * combo value is a real hand-density probability, not a deck-count power law;
///  * agent value is continuous in card value and current HP rather than a flat
///    bonus per enemy agent.
/// </summary>
public static class Eval
{
    // ---- tuning knobs -------------------------------------------------------
    // Each knob is read through Weights.G(key, default). In the submission build
    // Weights.G is a pass-through, so the literal in each call below IS the
    // value that ships. They stay `static readonly` rather than `const` because
    // the development tree's tuning harness rebinds them; the JIT folds a
    // static readonly double after class init either way, so the hot path is
    // identical.
    public static readonly double Scale = Weights.G("eval.Scale", 780.0);          // sigmoid e-fold, in raw points

    public static readonly double PrestigeBase = Weights.G("eval.PrestigeBase", 6.0);     // per-point weight at 0 progress
    public static readonly double PrestigeSlope = Weights.G("eval.PrestigeSlope", 34.0);  // extra per-point weight at full progress
    public static readonly double TempoOffset = Weights.G("eval.TempoOffset", 1.0);       // "+1 prestige is roughly break-even"

    public static readonly double Lead40Bonus = Weights.G("eval.Lead40Bonus", 900.0);     // we are >=40 and ahead
    public static readonly double Behind40Penalty = Weights.G("eval.Behind40Penalty", 1400.0);
    public static readonly double ApproachStep = Weights.G("eval.ApproachStep", 22.0);    // per point of opponent closeness to 40
    public static readonly double OwnApproachStep = Weights.G("eval.OwnApproachStep", 14.0);
    public static readonly double Danger80Step = Weights.G("eval.Danger80Step", 120.0);   // opponent in the 76-79 window
    public static readonly double Own80Step = Weights.G("eval.Own80Step", 70.0);

    public static readonly double PatronDiff = Weights.G("eval.PatronDiff", 55.0);
    public static readonly double[] EnemyPatronDist =
    {
        0.0,
        Weights.G("eval.EnemyPatronDist1", 2000.0),
        Weights.G("eval.EnemyPatronDist2", 260.0),
        Weights.G("eval.EnemyPatronDist3", 40.0),
        0.0,
    };
    public static readonly double[] OwnPatronDist =
    {
        0.0,
        Weights.G("eval.OwnPatronDist1", 900.0),
        Weights.G("eval.OwnPatronDist2", 160.0),
        Weights.G("eval.OwnPatronDist3", 30.0),
        0.0,
    };
    public static readonly double CrowsHoldPenalty = Weights.G("eval.CrowsHoldPenalty", 90.0);   // decays with game progress
    public static readonly double AnseiFavourBonus = Weights.G("eval.AnseiFavourBonus", 25.0);

    // Trades deck quality against prestige. At 1.0 an 88-value card is worth
    // ~14 early prestige points, i.e. deliberately prestige-greedy relative to a
    // deck-building-first valuation. The most sensitive knob in the table.
    public static readonly double DeckWeight = Weights.G("eval.DeckWeight", 1.8);
    public static readonly double DeckNorm = Weights.G("eval.DeckNorm", 16.0);         // deck size at which no dilution penalty applies
    public static readonly double ComboWeight = Weights.G("eval.ComboWeight", 260.0);
    public static readonly double BewildermentExtra = Weights.G("eval.BewildermentExtra", 22.0); // on top of the negative card value

    public static readonly double MyAgentCard = Weights.G("eval.MyAgentCard", 0.90);
    public static readonly double MyAgentHp = Weights.G("eval.MyAgentHp", 7.0);
    public static readonly double MyAgentTaunt = Weights.G("eval.MyAgentTaunt", 12.0);
    public static readonly double EnemyAgentCard = Weights.G("eval.EnemyAgentCard", 1.05);
    public static readonly double EnemyAgentHp = Weights.G("eval.EnemyAgentHp", 8.0);
    public static readonly double EnemyAgentFlat = Weights.G("eval.EnemyAgentFlat", 15.0);

    public static readonly double TavernDeny = Weights.G("eval.TavernDeny", 0.40);
    public static readonly double TavernDenyAffinity = Weights.G("eval.TavernDenyAffinity", 1.10);

    public static readonly double CoinValue = Weights.G("eval.CoinValue", 4.0);
    public static readonly double PatronCallValue = Weights.G("eval.PatronCallValue", 22.0);
    public static readonly double UpcomingDrawValue = Weights.G("eval.UpcomingDrawValue", 0.35);

    // Combo-density mixture over P(X>=2), P(X>=3), P(X>=4).
    public static readonly double ComboGe2 = Weights.G("eval.ComboGe2", 0.55);
    public static readonly double ComboGe3 = Weights.G("eval.ComboGe3", 0.30);
    public static readonly double ComboGe4 = Weights.G("eval.ComboGe4", 0.15);

    // Discount on the opponent's banked coins when it is not our turn.
    public static readonly double EnemyCoinValue = Weights.G("eval.EnemyCoinValue", 0.5);

    // Scratch buffers. GameRunner runs one bot instance per worker thread, so
    // these must not be shared across threads.
    [ThreadStatic] private static double[]? _myDeckCountTls;
    [ThreadStatic] private static double[]? _oppDeckCountTls;

    public static double Evaluate(SeededGameState s, PlayerEnum me)
    {
        var end = s.GameEndState;
        if (end != null)
        {
            if (end.Winner == me) return 1.0;
            if (end.Winner == PlayerEnum.NO_PLAYER_SELECTED) return 0.5;
            return 0.0;
        }

        bool myTurn = s.CurrentPlayer.PlayerID == me;
        SerializedPlayer mine = myTurn ? s.CurrentPlayer : s.EnemyPlayer;
        SerializedPlayer theirs = myTurn ? s.EnemyPlayer : s.CurrentPlayer;

        double raw = 0.0;

        // ---- prestige / win-condition race ---------------------------------
        // Mid-turn, unspent Power still becomes prestige at end of turn (minus
        // whatever the opponent's taunt agents soak up on the way out).
        double myEff = mine.Prestige;
        double oppEff = theirs.Prestige;
        if (myTurn)
        {
            int taunt = 0;
            for (int i = 0; i < theirs.Agents.Count; i++)
                if (theirs.Agents[i].RepresentingCard.Taunt) taunt += theirs.Agents[i].CurrentHp;
            myEff += Math.Max(0, mine.Power - taunt);
        }
        else
        {
            int taunt = 0;
            for (int i = 0; i < mine.Agents.Count; i++)
                if (mine.Agents[i].RepresentingCard.Taunt) taunt += mine.Agents[i].CurrentHp;
            oppEff += Math.Max(0, theirs.Power - taunt);
        }

        if (myEff >= 80) return 0.995;
        if (oppEff >= 80) return 0.005;

        double maxP = Math.Max(myEff, oppEff);
        double progress = Math.Min(1.0, maxP / 40.0);
        double prestigeWeight = PrestigeBase + PrestigeSlope * progress;
        raw += prestigeWeight * (myEff - oppEff - TempoOffset);

        // The 40 threshold: crossing it while strictly ahead wins at the start of
        // our next turn unless the opponent matches during their single reply.
        if (myEff >= 40.0 && myEff > oppEff) raw += Lead40Bonus;
        if (oppEff >= 40.0 && oppEff > myEff) raw -= Behind40Penalty;

        double oppDist = 40.0 - oppEff;
        if (oppDist > 0 && oppDist <= 12) raw -= (13.0 - oppDist) * ApproachStep;
        double myDist = 40.0 - myEff;
        if (myDist > 0 && myDist <= 12) raw += (13.0 - myDist) * OwnApproachStep;

        // 80 danger zone
        if (oppEff >= 76) raw -= Danger80Step * (oppEff - 75.0);
        if (myEff >= 76) raw += Own80Step * (myEff - 75.0);

        // ---- patrons --------------------------------------------------------
        int myFav = 0, oppFav = 0, myCalls = 0, oppCalls = 0, nonTreasury = 0;
        foreach (var kv in s.PatronStates.All)
        {
            PatronId p = kv.Key;
            if (p == PatronId.TREASURY) continue;
            nonTreasury++;
            PlayerEnum owner = kv.Value;
            int flipCost = p == PatronId.ANSEI ? 1 : 2;

            if (owner == me)
            {
                myFav++;
                oppCalls += flipCost;
                if (p == PatronId.DUKE_OF_CROWS)
                    raw -= CrowsHoldPenalty * (1.0 - 0.6 * progress);
                else if (p == PatronId.ANSEI)
                    raw += AnseiFavourBonus;
            }
            else if (owner == PlayerEnum.NO_PLAYER_SELECTED)
            {
                myCalls += 1;
                oppCalls += 1;
            }
            else
            {
                oppFav++;
                myCalls += flipCost;
                if (p == PatronId.DUKE_OF_CROWS)
                    raw += CrowsHoldPenalty * (1.0 - 0.6 * progress);
                else if (p == PatronId.ANSEI)
                    raw -= AnseiFavourBonus;
            }
        }

        if (nonTreasury > 0 && myFav == nonTreasury) return 0.995;
        if (nonTreasury > 0 && oppFav == nonTreasury) return 0.005;

        raw += (myFav - oppFav) * PatronDiff;
        if (oppCalls >= 1 && oppCalls < EnemyPatronDist.Length) raw -= EnemyPatronDist[oppCalls];
        if (myCalls >= 1 && myCalls < OwnPatronDist.Length) raw += OwnPatronDist[myCalls];

        // ---- decks ----------------------------------------------------------
        double[] myDeckCount = _myDeckCountTls ??= new double[16];
        double[] oppDeckCount = _oppDeckCountTls ??= new double[16];
        raw += DeckWeight * (DeckScore(mine, myDeckCount) - DeckScore(theirs, oppDeckCount));

        // ---- agents ---------------------------------------------------------
        for (int i = 0; i < mine.Agents.Count; i++)
        {
            var a = mine.Agents[i];
            raw += CardValues.DeckValue(a.RepresentingCard) * MyAgentCard + a.CurrentHp * MyAgentHp;
            if (a.RepresentingCard.Taunt) raw += MyAgentTaunt;
        }
        for (int i = 0; i < theirs.Agents.Count; i++)
        {
            var a = theirs.Agents[i];
            raw -= CardValues.DeckValue(a.RepresentingCard) * EnemyAgentCard
                   + a.CurrentHp * EnemyAgentHp + EnemyAgentFlat;
        }

        // ---- tavern denial ---------------------------------------------------
        int oppCards = 0;
        for (int i = 0; i < 16; i++) oppCards += (int)oppDeckCount[i];
        if (oppCards < 1) oppCards = 1;
        var tav = s.TavernAvailableCards;
        for (int i = 0; i < tav.Count; i++)
        {
            var c = tav[i];
            double v = CardValues.DeckValue(c);
            if (v <= 0) continue;
            double affinity = c.Deck == PatronId.TREASURY
                ? 0.0
                : oppDeckCount[(int)c.Deck] / oppCards;
            raw -= v * (TavernDeny + TavernDenyAffinity * affinity);
        }

        // ---- intra-turn resources (only meaningful at a mid-turn horizon) ----
        if (myTurn)
        {
            raw += mine.Coins * CoinValue;
            raw += mine.PatronCalls * PatronCallValue;
        }
        else
        {
            raw -= theirs.Coins * CoinValue * EnemyCoinValue;
        }

        for (int i = 0; i < mine.KnownUpcomingDraws.Count && i < 4; i++)
            raw += CardValues.HandValue(mine.KnownUpcomingDraws[i]) * UpcomingDrawValue;

        return 1.0 / (1.0 + Math.Exp(-raw / Scale));
    }

    /// <summary>
    /// Deck quality with a real combo-density term. deckCount is filled in as a
    /// side effect (used by the tavern-denial term).
    /// </summary>
    private static double DeckScore(SerializedPlayer p, double[] deckCount)
    {
        for (int i = 0; i < 16; i++) deckCount[i] = 0;

        double sum = 0.0;
        int n = 0;
        n += Accumulate(p.Hand, deckCount, ref sum);
        n += Accumulate(p.DrawPile, deckCount, ref sum);
        n += Accumulate(p.CooldownPile, deckCount, ref sum);
        n += Accumulate(p.Played, deckCount, ref sum);
        for (int i = 0; i < p.Agents.Count; i++)
        {
            var c = p.Agents[i].RepresentingCard;
            sum += CardValues.DeckValue(c);
            n++;
            int d = (int)c.Deck;
            if ((uint)d < 16) deckCount[d] += 1;
        }

        if (n < 1) n = 1;
        double norm = Math.Sqrt(Math.Max(1.0, n / DeckNorm));
        double value = sum / norm;

        // Combo density: probability of drawing >= k cards of a deck into a
        // 5-card hand, weighted by that deck's combo payoff strength.
        for (int d = 0; d < 16; d++)
        {
            double c = deckCount[d];
            if (c < 2) continue;
            double factor = CardValues.ComboFactor[d];
            if (factor <= 0) continue;

            double q = 1.0 - c / n;
            if (q < 0) q = 0;
            double q2 = q * q, q3 = q2 * q, q4 = q3 * q, q5 = q4 * q;
            double pp = 1.0 - q;
            double p0 = q5;
            double p1 = 5 * pp * q4;
            double p2 = 10 * pp * pp * q3;
            double p3 = 10 * pp * pp * pp * q2;
            double ge2 = 1.0 - p0 - p1;
            double ge3 = ge2 - p2;
            double ge4 = ge3 - p3;
            if (ge3 < 0) ge3 = 0;
            if (ge4 < 0) ge4 = 0;

            value += ComboWeight * factor * (ComboGe2 * ge2 + ComboGe3 * ge3 + ComboGe4 * ge4);
        }

        return value;
    }

    private static int Accumulate(List<UniqueCard> cards, double[] deckCount, ref double sum)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            double v = CardValues.DeckValue(c);
            sum += v;
            if (c.CommonId == CardId.BEWILDERMENT) sum -= BewildermentExtra;
            int d = (int)c.Deck;
            if ((uint)d < 16) deckCount[d] += 1;
        }
        return cards.Count;
    }
}

// ==========================================================================
// from PrimalKyogreCards.cs
// ==========================================================================

/// <summary>
/// PrimalKyogre's own card valuation. Flat arrays indexed by (int)CardId — no string
/// scans, no dictionary lookups on the hot path.
///
/// Scale is a continuous 0-100 "quality of this card as a deck slot" measure
/// (not a 5-bucket tier), so ties are rare and marginal buy decisions have real
/// resolution. Bewilderment is negative. Every number here is original to this
/// agent; in particular the Orgnum and Saint Alessia decks are priced up
/// relative to a conventional tier list, which is what the patron draft order in
/// the entry point rests on.
/// </summary>
public static class CardValues
{
    public const int CardCount = 125;

    /// <summary>Value of owning this card in our deck (0-100 scale, curses negative).</summary>
    public static readonly double[] Deck = new double[CardCount];

    /// <summary>Value of this card sitting in hand right now (cheap, immediate-use bias).</summary>
    public static readonly double[] InHand = new double[CardCount];

    /// <summary>How strongly this deck rewards concentrating buys in it (combo payoff density).</summary>
    public static readonly double[] ComboFactor = new double[16];

    static CardValues()
    {
        // ---- Treasury -------------------------------------------------------
        S(CardId.GOLD, 6, 9);
        S(CardId.WRIT_OF_COIN, 22, 26);
        S(CardId.BARTERER, 34, 34);
        S(CardId.BLACK_SACRAMENT, 30, 30);
        S(CardId.HARVEST_SEASON, 24, 24);
        S(CardId.AMBUSH, 42, 42);
        S(CardId.BLACKMAIL, 20, 20);
        S(CardId.RAGPICKER, 36, 36);
        S(CardId.TITHE, 44, 44);
        S(CardId.IMPRISONMENT, 30, 30);

        // ---- Orgnum (cheap, prestige-dense; the field under-rates this deck) -
        S(CardId.SEA_ELF_RAID, 10, 12);
        S(CardId.GHOSTSCALE_SEA_SERPENT, 62, 58);
        S(CardId.MAORMER_BOARDING_PARTY, 60, 56);
        S(CardId.SEA_RAIDERS_GLORY, 55, 52);
        S(CardId.SEA_SERPENT_COLOSSUS, 68, 62);
        S(CardId.SUMMERSET_SACKING, 62, 58);
        S(CardId.KING_ORGNUMS_COMMAND, 48, 48);
        S(CardId.MAORMER_CUTTER, 38, 38);
        S(CardId.PYANDONEAN_WAR_FLEET, 55, 52);
        S(CardId.SERPENTPROW_SCHOONER, 52, 50);
        S(CardId.SERPENTGUARD_RIDER, 74, 68);
        S(CardId.SNAKESKIN_FREEBOOTER, 80, 74);
        S(CardId.STORM_SHARK_WAVECALLER, 70, 64);

        // ---- Pelin ----------------------------------------------------------
        S(CardId.FORTIFY, 8, 10);
        S(CardId.THE_PORTCULLIS, 44, 42);
        S(CardId.REINFORCEMENTS, 55, 52);
        S(CardId.LEGIONS_ARRIVAL, 60, 56);
        S(CardId.ARCHERS_VOLLEY, 58, 55);
        S(CardId.SIEGE_WEAPON_VOLLEY, 68, 64);
        S(CardId.THE_ARMORY, 78, 72);
        S(CardId.RALLY, 88, 84);
        S(CardId.SHIELD_BEARER, 46, 46);
        S(CardId.BANGKORAI_SENTRIES, 66, 62);
        S(CardId.KNIGHTS_OF_SAINT_PELIN, 70, 66);
        S(CardId.BANNERET, 86, 82);
        S(CardId.KNIGHT_COMMANDER, 90, 86);

        // ---- Rajhin ---------------------------------------------------------
        S(CardId.SWIPE, 8, 10);
        S(CardId.BEWILDERMENT, -30, -25);
        S(CardId.SLIGHT_OF_HAND, 46, 44);
        S(CardId.MOONLIT_ILLUSION, 40, 40);
        S(CardId.JEERING_SHADOW, 50, 46);
        S(CardId.PROWLING_SHADOW, 60, 56);
        S(CardId.GRAND_LARCENY, 74, 72);
        S(CardId.POUNCE_AND_PROFIT, 70, 68);
        S(CardId.STUBBORN_SHADOW, 62, 58);
        S(CardId.BAG_OF_TRICKS, 30, 30);
        S(CardId.JARRING_LULLABY, 62, 60);
        S(CardId.RINGS_GUILE, 32, 32);
        S(CardId.SHADOWS_SLUMBER, 66, 64);
        S(CardId.TWILIGHT_REVELRY, 60, 58);

        // ---- Ansei ----------------------------------------------------------
        S(CardId.WAY_OF_THE_SWORD, 12, 14);
        S(CardId.GRAND_ORATORY, 36, 36);
        S(CardId.BATTLE_MEDITATION, 26, 26);
        S(CardId.CONQUEST, 62, 58);
        S(CardId.WARRIOR_WAVE, 58, 56);
        S(CardId.SHEHAI_SUMMONING, 50, 46);
        S(CardId.HIRAS_END, 78, 74);
        S(CardId.MARCH_ON_HATTU, 72, 68);
        S(CardId.HEL_SHIRA_HERALD, 62, 58);
        S(CardId.NO_SHIRA_POET, 56, 52);
        S(CardId.ANSEI_ASSAULT, 72, 68);
        S(CardId.ANSEIS_VICTORY, 78, 74);

        // ---- Duke of Crows --------------------------------------------------
        S(CardId.PECK, 8, 10);
        S(CardId.SCRATCH, 62, 60);
        S(CardId.TOLL_OF_FLESH, 68, 66);
        S(CardId.TOLL_OF_SILVER, 70, 68);
        S(CardId.MURDER_OF_CROWS, 72, 70);
        S(CardId.LAW_OF_SOVEREIGN_ROOST, 34, 34);
        S(CardId.POOL_OF_SHADOW, 62, 60);
        S(CardId.BLACKFEATHER_KNAVE, 70, 66);
        S(CardId.BLACKFEATHER_BRIGAND, 58, 54);
        S(CardId.BLACKFEATHER_KNIGHT, 68, 64);
        S(CardId.PLUNDER, 66, 68);
        S(CardId.PILFER, 60, 62);
        S(CardId.SQUAWKING_ORATORY, 70, 70);

        // ---- Saint Alessia (newest deck) -------------------------------------
        S(CardId.ALESSIAN_REBEL, 14, 16);
        S(CardId.SOLDIER_OF_THE_EMPIRE, 42, 40);
        S(CardId.CHAINBREAKER_SERGEANT, 56, 52);
        S(CardId.CHAINBREAKER_CAPTAIN, 62, 58);
        S(CardId.SAINTS_WRATH, 48, 46);
        S(CardId.PRIESTESS_OF_THE_EIGHT, 66, 62);
        S(CardId.MORIHAUS_SACRED_BULL, 64, 60);
        S(CardId.MORIHAUS_THE_ARCHER, 58, 54);
        S(CardId.AYLEID_DEFECTOR, 58, 58);
        S(CardId.AYLEID_QUARTERMASTER, 62, 62);
        S(CardId.PELINAL_WHITESTRAKE, 74, 70);
        S(CardId.WHITESTRAKE_ASCENDANT, 80, 76);

        // ---- Out-of-pool decks (kept current in case the 2026 pool changes) --
        S(CardId.CURRENCY_EXCHANGE, 88, 84);
        S(CardId.LUXURY_EXPORTS, 10, 12);
        S(CardId.OATHMAN, 62, 60);
        S(CardId.EBONY_MINE, 40, 40);
        S(CardId.HLAALU_COUNCILOR, 62, 70);
        S(CardId.HLAALU_KINSMAN, 60, 68);
        S(CardId.HOUSE_EMBASSY, 55, 58);
        S(CardId.HOUSE_MARKETPLACE, 58, 56);
        S(CardId.KWAMA_EGG_MINE, 36, 36);
        S(CardId.HIRELING, 30, 30);
        S(CardId.HOSTILE_TAKEOVER, 62, 60);
        S(CardId.CUSTOMS_SEIZURE, 14, 16);
        S(CardId.GOODS_SHIPMENT, 8, 10);

        S(CardId.MIDNIGHT_RAID, 82, 80);
        S(CardId.BLOOD_SACRIFICE, 80, 76);
        S(CardId.BLOODY_OFFERING, 80, 76);
        S(CardId.BONFIRE, 45, 44);
        S(CardId.BRIARHEART_RITUAL, 50, 48);
        S(CardId.CLANWITCH, 52, 50);
        S(CardId.ELDER_WITCH, 55, 52);
        S(CardId.HAGRAVEN, 55, 52);
        S(CardId.HAGRAVEN_MATRON, 68, 64);
        S(CardId.IMPERIAL_PLUNDER, 55, 52);
        S(CardId.IMPERIAL_SPOILS, 52, 50);
        S(CardId.KARTH_MANHUNTER, 50, 48);
        S(CardId.WAR_SONG, 8, 10);

        S(CardId.PROPHESY, 40, 42);
        S(CardId.SCRYING_GLOBE, 45, 44);
        S(CardId.THE_DREAMING_CAVE, 45, 44);
        S(CardId.AUGURS_COUNSEL, 30, 30);
        S(CardId.PSIJIC_RELICMASTER, 55, 52);
        S(CardId.SAGE_COUNSEL, 30, 30);
        S(CardId.PRESCIENCE, 40, 42);
        S(CardId.PSIJIC_APPRENTICE, 45, 44);
        S(CardId.CEPORAHS_INSIGHT, 45, 44);
        S(CardId.PSIJICS_INSIGHT, 50, 48);
        S(CardId.TIME_MASTERY, 55, 52);
        S(CardId.MAINLAND_INQUIRIES, 8, 10);

        // ---- combo payoff density per deck ----------------------------------
        ComboFactor[(int)PatronId.ANSEI] = 0.75;
        ComboFactor[(int)PatronId.DUKE_OF_CROWS] = 1.30;
        ComboFactor[(int)PatronId.RAJHIN] = 0.90;
        ComboFactor[(int)PatronId.PSIJIC] = 1.00;
        ComboFactor[(int)PatronId.ORGNUM] = 1.15;
        ComboFactor[(int)PatronId.HLAALU] = 1.20;
        ComboFactor[(int)PatronId.PELIN] = 1.05;
        ComboFactor[(int)PatronId.RED_EAGLE] = 1.10;
        ComboFactor[(int)PatronId.TREASURY] = 0.00; // Treasury cards have no combo effects
        ComboFactor[(int)PatronId.SAINT_ALESSIA] = 0.95;

        ApplyOverrides();
    }

    /// <summary>
    /// Overlay tuner overrides on top of the compiled-in tables. Inert in the
    /// submission build, where Weights.Count is always zero so this returns
    /// immediately; kept so the shipped tables come out of the same code path
    /// the tuning runs used.
    /// </summary>
    private static void ApplyOverrides()
    {
        if (Weights.Count == 0) return;

        foreach (CardId id in Enum.GetValues(typeof(CardId)))
        {
            int i = (int)id;
            if ((uint)i >= CardCount) continue;
            string n = id.ToString();
            Deck[i] = Weights.G("card." + n + ".deck", Deck[i]);
            InHand[i] = Weights.G("card." + n + ".hand", InHand[i]);
        }
        for (int i = 0; i < CardCount; i++)
        {
            Deck[i] = Weights.G("card." + i + ".deck", Deck[i]);
            InHand[i] = Weights.G("card." + i + ".hand", InHand[i]);
        }
        foreach (PatronId p in Enum.GetValues(typeof(PatronId)))
        {
            int i = (int)p;
            if ((uint)i >= 16) continue;
            ComboFactor[i] = Weights.G("combo." + p, ComboFactor[i]);
        }
        for (int i = 0; i < 16; i++)
            ComboFactor[i] = Weights.G("combo." + i, ComboFactor[i]);
    }

    private static void S(CardId id, double deck, double inHand)
    {
        Deck[(int)id] = deck;
        InHand[(int)id] = inHand;
    }

    public static double DeckValue(UniqueCard c)
    {
        int i = (int)c.CommonId;
        return (uint)i < CardCount ? Deck[i] : 0.0;
    }

    public static double HandValue(UniqueCard c)
    {
        int i = (int)c.CommonId;
        return (uint)i < CardCount ? InHand[i] : 0.0;
    }

    public static double Combo(PatronId p)
    {
        int i = (int)p;
        return (uint)i < 16 ? ComboFactor[i] : 1.0;
    }
}

// ==========================================================================
// from PrimalKyogreWeights.cs
// ==========================================================================

/// <summary>
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
