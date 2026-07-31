using ScriptsOfTribute;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Serializers;

namespace Riptide;

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
///  1. as the PUCT prior in the tree (and unlike the 2025 champion, the prior
///     actually influences selection here);
///  2. as the rollout policy (greedy argmax) to end of turn;
///  3. as the panic fallback when the turn clock is nearly exhausted.
///
/// Note this policy never *forces* a card play outside the search — combo
/// ordering is a search decision, which is the biggest hole in the 2025 winner.
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
