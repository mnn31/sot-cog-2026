using System.Globalization;

namespace Riptide;

/// <summary>
/// External weight table for the evaluation function.
///
/// Every tunable number in <see cref="Eval"/> and <see cref="CardValues"/> is
/// read through <see cref="G"/>, which returns the compiled-in default unless an
/// override is present in the file named by the <c>RIPTIDE_WEIGHTS</c>
/// environment variable (absolute path). That makes a tuning candidate a plain
/// file instead of a rebuild.
///
/// Format: a flat JSON object of <c>"key": number</c> pairs — valid JSON, so the
/// tuner can emit it with <c>json.dump</c>, but parsed here by a ~40 line
/// hand-rolled reader so the bot keeps zero package dependencies (the Bots
/// project is netstandard2.1 and does not reference System.Text.Json).
///
/// Keys:
///   eval.&lt;FieldName&gt;            all Eval scalars, e.g. "eval.DeckWeight"
///   eval.EnemyPatronDist1..3     entries of the patron-distance arrays
///   eval.OwnPatronDist1..3
///   card.&lt;CARD_ID&gt;.deck         per-card deck value, e.g. "card.RALLY.deck"
///   card.&lt;CARD_ID&gt;.hand         per-card in-hand value
///   card.&lt;index&gt;.deck|hand      same, by raw (int)CardId — applied after names
///   combo.&lt;PATRON_ID&gt;           per-deck combo payoff factor
///
/// Loading happens exactly once per process (CLR static constructor) and is
/// forced from the <see cref="Riptide"/> constructor so a malformed file fails
/// loudly up front instead of silently degrading to defaults mid-tournament.
/// </summary>
public static class Weights
{
    private static readonly Dictionary<string, double> Map =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Path we were pointed at, or "" when RIPTIDE_WEIGHTS is unset.</summary>
    public static readonly string SourcePath = "";

    /// <summary>Number of overrides successfully parsed.</summary>
    public static readonly int Count;

    /// <summary>Non-null when the file existed but could not be read/parsed.</summary>
    public static readonly string? Error;

    static Weights()
    {
        string? p = Environment.GetEnvironmentVariable("RIPTIDE_WEIGHTS");
        if (p == null || p.Trim().Length == 0) return;
        SourcePath = p.Trim();
        try
        {
            Parse(File.ReadAllText(SourcePath), Map);
            Count = Map.Count;
        }
        catch (Exception e)
        {
            Error = e.GetType().Name + ": " + e.Message;
            Map.Clear();
            Count = 0;
        }
    }

    /// <summary>Override for <paramref name="key"/>, else the compiled-in default.</summary>
    public static double G(string key, double def)
    {
        double v;
        return Map.TryGetValue(key, out v) ? v : def;
    }

    private static int _announced;

    /// <summary>
    /// Forces the weight file, <see cref="Eval"/> and <see cref="CardValues"/> to
    /// initialise, and prints one audit line to <b>stderr</b> (never stdout —
    /// GameRunner's result lines are parsed from stdout).
    /// </summary>
    public static void EnsureLoaded()
    {
        if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0) return;
        _ = Eval.Scale + CardValues.Deck[0] + CardValues.ComboFactor[0];
        if (SourcePath.Length == 0) return;
        if (Error != null)
        {
            Console.Error.WriteLine("[riptide] WEIGHTS LOAD FAILED '" + SourcePath
                                    + "': " + Error + " — using compiled-in defaults");
        }
        else
        {
            Console.Error.WriteLine("[riptide] weights '" + SourcePath + "' overrides=" + Count
                + " DeckWeight=" + Eval.DeckWeight.ToString("G6", CultureInfo.InvariantCulture)
                + " Scale=" + Eval.Scale.ToString("G6", CultureInfo.InvariantCulture));
        }
    }

    // ---- minimal flat-JSON reader ------------------------------------------

    private static void Parse(string t, Dictionary<string, double> into)
    {
        int i = 0;
        SkipWs(t, ref i);
        if (i < t.Length && t[i] == '{') i++;
        while (i < t.Length)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) break;
            char ch = t[i];
            if (ch == '}') break;
            if (ch == ',') { i++; continue; }
            if (ch != '"') throw new FormatException("expected '\"' at offset " + i);

            i++;
            int ks = i;
            while (i < t.Length && t[i] != '"') i++;
            if (i >= t.Length) throw new FormatException("unterminated key at offset " + ks);
            string key = t.Substring(ks, i - ks);
            i++;

            SkipWs(t, ref i);
            if (i >= t.Length || t[i] != ':') throw new FormatException("expected ':' after key '" + key + "'");
            i++;
            SkipWs(t, ref i);

            int vs = i;
            while (i < t.Length && t[i] != ',' && t[i] != '}' && !char.IsWhiteSpace(t[i])) i++;
            string val = t.Substring(vs, i - vs);
            double d;
            if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number for key '" + key + "': '" + val + "'");
            into[key] = d;
        }
    }

    private static void SkipWs(string t, ref int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
    }
}
