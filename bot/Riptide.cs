using System.Diagnostics;
using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Serializers;

namespace Riptide;

/// <summary>
/// Riptide — CoG 2026 Tales of Tribute entry, v1.
///
/// Design in one paragraph: deliberate patron draft; a determinized ensemble
/// (K worlds, common random numbers) of PUCT best-first searches with max backup
/// over complete turn plans; our own continuous CardId-indexed evaluation with a
/// real combo-density term, an explicit 40-prestige race model, and two-sided
/// patron pressure; and a conservative cumulative turn clock with a greedy
/// policy escape hatch.
/// </summary>
public class Riptide : AI
{
    // ---- patron draft -------------------------------------------------------
    // 2026 pool: PELIN, DUKE_OF_CROWS, ANSEI, RAJHIN, ORGNUM, SAINT_ALESSIA.
    // Orgnum first is the one point of agreement between the two 2025 bots that
    // beat the champion head to head. Alessia second because the 2025 field is
    // structurally blind to it (BestMCTS3 refuses to draft it, HQL has almost no
    // trained values for it), and we price it properly.
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
    // v2 change: the per-decision slice is HARDWARE-ADAPTIVE.
    //
    // v1 used a flat `min(0.80, left/4)` slice, sized on a fast Mac. It has two
    // failure modes on a slower or contended machine: the search stops long
    // before it has solved the reachable turn plan space, AND the turn's budget
    // is left partly unspent because no mechanism hands the unused time of a
    // fast-solving decision to a slow one. Measured cost: 52% vs BestMCTS3 on
    // the Mac, 37.5% on a contended box with the same seeds.
    //
    // The slice is now `SliceBoost x (what is actually left) / (decisions this
    // turn is still expected to contain)`, with two measured floors and four
    // independent ceilings:
    //   (1) decisions-per-turn, EWMA across turns and deliberately
    //       over-estimated — under-estimating starves the end of the turn,
    //       where the buys and patron calls live;
    //   (2) `left` is recomputed from the engine's own clock every call, so a
    //       decision that solved in 40 ms hands its unused share straight to the
    //       decisions after it — the redistribution v1 had no mechanism for;
    //   (3) floor A: the measured cost of an actual SOLVE on this machine
    //       (converged decisions only), so on slow hardware we spend what a
    //       solve costs instead of stopping just short of one;
    //   (4) floor B: the live iteration rate, so a slice always affords several
    //       full root sweeps per determinization — below that the root aggregate
    //       is single-world noise and worse than the greedy policy;
    //   (5) a fixed-fraction cap on the slice, inherited from v1's `left/4`, so
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
    // v1's `left/4` had one property worth keeping: capping a slice at a FIXED
    // FRACTION of what is left makes the remaining budget decay geometrically, so
    // it can never reach zero and the turn can never run out of decisions no
    // matter how badly the count was estimated. Measured: without this, six
    // times oversubscribed, v2 spent 9.03 s of a 9.25 s budget and dropped up to
    // 12 tail decisions into the greedy fallback, where v1 dropped none. It only
    // binds late in a turn, where the share term has already grown past it.
    public const double MaxSliceFraction = 0.25;

    // Decisions-per-turn estimate. Measured on the Mac: 7-12 searched decisions
    // per turn, mean ~9.5.
    public const double DefaultDecisionsPerTurn = 9.0;
    public const double DecisionsEwmaAlpha = 0.25;
    public const double DecisionCountSafety = 1.15;   // over-estimate the tail of the turn
    public const double DecisionCountPad = 0.50;

    // What it costs THIS machine to solve a decision outright, measured only over
    // decisions that actually converged (so it is never contaminated by a slice
    // we cut short ourselves). Raises the slice floor on slow hardware: if a
    // solve here costs 1.5 s, handing a decision 0.9 s reliably buys a truncated
    // search, and truncation is where the pod regression came from.
    public const double DefaultSolveSeconds = 0.25;
    public const double SolveEwmaAlpha = 0.30;
    public const double SolveFloorFactor = 1.00;
    public const double SolveFloorFraction = 0.25;    // ... but never > 25% of what is left

    // Search throughput on this machine. Measured on the Mac: ~37 k
    // iterations/s; a contended or slower box runs a fraction of that, and the
    // sweep floor below grows in exact proportion.
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

    // Off unless RIPTIDE_DIAG=1. Used to verify we are actually spending the
    // turn budget (and never overrunning it) without touching the hot path.
    private static readonly bool Diag =
        Environment.GetEnvironmentVariable("RIPTIDE_DIAG") == "1";
    private int _turnDecisions;
    private int _turnIterations;
    private int _turnNodes;
    private int _turnUnconverged;
    private double _turnMaxOvershoot;

    public Riptide()
    {
        // Load the external weight table (RIPTIDE_WEIGHTS) exactly once per
        // process, before any search runs, and force Eval/CardValues to
        // initialise so a malformed file fails loudly here rather than silently
        // reverting to defaults halfway through a tournament.
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
            Log("Riptide: recovered from " + e.GetType().Name + ": " + e.Message);
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
                "[riptide] turn used=" + _turnUsedSeconds.ToString("F2") + "s"
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
            for (int i = 0; i < _seeds.Length; i++) _seeds[i] = NextRandom() | 1UL;
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
        //     v1 could not do this — its 0.80 s cap was hit whether or not the
        //     rest of the turn had 8 spare seconds sitting idle.
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
        // never more than a fixed fraction of what is left. This is the one part
        // of v1's `left/4` worth keeping — it makes the remaining budget decay
        // geometrically instead of linearly, so no misestimate of the decision
        // count can strand the end of the turn on the greedy fallback.
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
            Log("Riptide game end: recovered errors=" + _errorCount + " panic fallbacks=" + _panicCount);

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
            Console.Error.WriteLine("[riptide] FATAL " + state.Reason + " as " + _me
                                    + " (turnUsed=" + _turnUsedSeconds.ToString("F2")
                                    + "s guard=" + _timeGuard.ToString("F2") + "s)");
        }
        Reset();
    }
}
