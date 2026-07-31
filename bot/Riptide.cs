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
    public const double TurnCeilingSeconds = 9.3;
    public const double TurnSafetyMargin = 0.70;   // subtracted from engine-reported remaining
    public const double MaxSliceSeconds = 0.80;
    public const double SliceDivisor = 4.0;
    public const double PanicThresholdSeconds = 0.30;

    private readonly Stopwatch _callClock = new Stopwatch();
    private double _turnUsedSeconds;
    // Sentinel below zero so that the first Play() of every turn is detected as a
    // turn start (remainingTime jumps back up when the engine resets the clock).
    private double _lastRemainingSeconds = -1.0;
    private double _turnBudgetSeconds = TurnCeilingSeconds;

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

    public Riptide()
    {
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

        if (chosen.Command == CommandEnum.END_TURN)
        {
            if (Diag)
            {
                Console.Error.WriteLine(
                    "[riptide] turn used=" + _turnUsedSeconds.ToString("F2") + "s"
                    + " budget=" + _turnBudgetSeconds.ToString("F2") + "s"
                    + " searched=" + _turnDecisions
                    + " iters=" + _turnIterations
                    + " nodes=" + _turnNodes
                    + " panic=" + _panicCount);
            }
            _turnUsedSeconds = 0.0;
            _lastRemainingSeconds = -1.0;
            _turnDecisions = 0;
            _turnIterations = 0;
            _turnNodes = 0;
        }
        return chosen;
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
            _turnUsedSeconds = 0.0;
            _turnBudgetSeconds = Math.Min(remainingSeconds - TurnSafetyMargin, TurnCeilingSeconds);
            for (int i = 0; i < _seeds.Length; i++) _seeds[i] = NextRandom() | 1UL;
        }
        else if (_turnBudgetSeconds <= 0.0)
        {
            _turnBudgetSeconds = Math.Min(remainingSeconds - TurnSafetyMargin, TurnCeilingSeconds);
        }

        // ---- rule short circuits (deliberately only two) --------------------
        if (possibleMoves.Count == 1) return possibleMoves[0];

        Move? trivial = TrivialChoice(gameState, possibleMoves);
        if (trivial != null) return trivial;

        // ---- budget ---------------------------------------------------------
        double engineLimit = remainingSeconds - TurnSafetyMargin;
        double selfLimit = _turnBudgetSeconds - _turnUsedSeconds;
        double left = Math.Min(engineLimit, selfLimit) - _callClock.Elapsed.TotalSeconds;

        if (left < PanicThresholdSeconds)
        {
            _panicCount++;
            return Policy.Best(possibleMoves, PolicyContext.From(gameState));
        }

        double slice = Math.Min(MaxSliceSeconds, left / SliceDivisor);
        double deadline = _callClock.Elapsed.TotalSeconds + slice;

        var search = new Search(_me, _callClock);
        Move picked = search.Run(gameState, possibleMoves, _seeds, deadline);
        _turnDecisions++;
        _turnIterations += search.Iterations;
        _turnNodes += search.NodesCreated;
        return picked;
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
        Reset();
    }
}
