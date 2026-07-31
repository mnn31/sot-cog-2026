using ScriptsOfTribute;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Serializers;

namespace Riptide;

/// <summary>
/// Leaf evaluation. Returns a win-probability-like score in (0,1) from the
/// perspective of <c>me</c>. Every backup site in the search uses this same
/// scale — there is no negation, no zero-sum mixing.
///
/// Structural differences vs the 2025 field:
///  * an explicit 40-prestige race model with graduated urgency on both sides
///    (the 2025 champion only panics at 76-79, i.e. the far less decisive race);
///  * patron distance is scored offensively as well as defensively;
///  * combo value is a real hand-density probability, not a deck-count power law;
///  * enemy agents are not multiplied by 2 with a flat +40 fear bonus.
/// </summary>
public static class Eval
{
    // ---- tuning knobs -------------------------------------------------------
    // These are `static readonly` rather than `const` so that every one of them
    // can be overridden from the RIPTIDE_WEIGHTS file (see Weights). The JIT
    // still folds a static readonly double after class init, so the hot path is
    // unchanged. The literal in each G(...) call is the compiled-in default and
    // is what ships when no weight file is set.
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
    // ~14 early prestige points, which is aggressively prestige-greedy; the 2025
    // champion sits at the opposite extreme (~50). Top tuning target.
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
