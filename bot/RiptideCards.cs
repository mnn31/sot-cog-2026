using ScriptsOfTribute;
using ScriptsOfTribute.Board.Cards;

namespace Riptide;

/// <summary>
/// Riptide's own card valuation. Flat arrays indexed by (int)CardId — no string
/// scans, no dictionary lookups on the hot path.
///
/// Scale is a continuous 0-100 "quality of this card as a deck slot" measure
/// (not a 5-bucket tier), so ties are rare and marginal buy decisions have real
/// resolution. Bewilderment is negative. Values are our own, informed by (but
/// deliberately not equal to) what the 2025 field published: in particular the
/// Orgnum and Saint Alessia decks are priced up, because the 2025 top four
/// either refuse to draft them (BestMCTS3) or have no trained values for them
/// (HQL_Plus).
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

        // ---- Saint Alessia (newest deck; the 2025 field is blind to it) ------
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
