using System.Diagnostics;
using ScriptsOfTribute;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.Serializers;

namespace Riptide;

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
/// Root values are averaged across determinizations.
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

    private readonly PlayerEnum _me;
    private readonly Stopwatch _clock;
    private double _deadlineSeconds;
    private int _nodesCreated;
    private bool _abort;

    public int Iterations { get; private set; }
    public int NodesCreated => _nodesCreated;

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

        List<Move> roots = TrimRoot(rootMoves, PolicyContext.From(gameState));
        int n = roots.Count;
        if (n == 1) return roots[0];

        int k = seeds.Length;
        var trees = new PlanNode?[k];
        var sum = new double[n];
        var count = new int[n];

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

        for (int i = 0; i < k; i++)
        {
            var t = trees[i];
            if (t == null) continue;
            for (int j = 0; j < n; j++)
            {
                if (t.ChildVisits[j] > 0)
                {
                    sum[j] += t.ChildValue[j];
                    count[j]++;
                }
            }
        }

        int best = -1;
        double bestVal = double.NegativeInfinity;
        for (int j = 0; j < n; j++)
        {
            if (count[j] == 0) continue;
            double v = sum[j] / count[j];
            // Prefer moves that every determinization agreed to look at.
            v += 0.0005 * count[j];
            if (v > bestVal) { bestVal = v; best = j; }
        }

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
