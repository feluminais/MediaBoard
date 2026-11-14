namespace CozyCouch.Core.Identification;

public static class TitleSimilarity
{
    /// <summary>
    /// Similarity of two titles in 0..1: <see cref="ScoringWeights.TitleSortShare"/> times the token-sort
    /// ratio plus <see cref="ScoringWeights.TitleSetShare"/> times the token-set ratio. Both parts ignore
    /// word order. The token-sort ratio is the Levenshtein ratio of the normalized words sorted
    /// alphabetically, so extra words (a sequel number) cost points; the token-set ratio compares the
    /// shared words with the rest and scores a title that contains the other's words as 1.0. Titles whose
    /// sequel numbers differ lose <see cref="ScoringWeights.NumberMismatchPenalty"/> (see <see cref="SameNumbers"/>).
    /// </summary>
    public static double Score(string a, string b) => ScoreNormalized(TitleNormalizer.Normalize(a), TitleNormalizer.Normalize(b));

    /// <summary>
    /// <see cref="Score"/> for two titles that went through <see cref="TitleNormalizer.Normalize"/> already, for
    /// callers that compare one title against many and normalize each only once. Without
    /// <paramref name="numbersMustAgree"/> the sequel-number penalty is left out, for a typed search where a
    /// missing number means "any of them".
    /// </summary>
    public static double ScoreNormalized(string na, string nb, bool numbersMustAgree = true)
    {
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        if (na == nb) return 1.0;

        var score = ScoringWeights.TitleSortShare * TokenSortRatio(na, nb)
                    + ScoringWeights.TitleSetShare * TokenSetRatio(na, nb);
        if (numbersMustAgree && !SameNumbers(na, nb)) score -= ScoringWeights.NumberMismatchPenalty;
        return Math.Clamp(score, 0.0, 1.0);
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["twenty"] = 20,
    };

    // "i", "v" and "x" alone are as often a letter or a name ("Malcolm X", "V for Vendetta"), so they count only
    // after a word that announces a number.
    private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["ii"] = 2, ["iii"] = 3, ["iv"] = 4, ["vi"] = 6, ["vii"] = 7, ["viii"] = 8, ["ix"] = 9,
        ["xi"] = 11, ["xii"] = 12, ["xiii"] = 13, ["xiv"] = 14, ["xv"] = 15,
    };

    private static readonly Dictionary<string, int> SingleLetterNumerals = new(StringComparer.Ordinal)
    {
        ["i"] = 1, ["v"] = 5, ["x"] = 10,
    };

    private static readonly HashSet<string> NumberLeads = new(StringComparer.Ordinal)
    {
        "part", "episode", "chapter", "volume", "vol",
    };

    /// <summary>
    /// True when two normalized titles carry the same sequel and part numbers: digits ("2"), number words up to
    /// twenty ("Four", "Eleven"), Roman numerals ("II"), and "I", "V" or "X" after "part", "episode", "chapter" or
    /// "volume" all count. So "Rocky 2" and "Rocky II" agree while "Open Season 2" and "Open Season" do not.
    /// </summary>
    public static bool SameNumbers(string na, string nb) => Numbers(na).SetEquals(Numbers(nb));

    private static HashSet<int> Numbers(string normalized)
    {
        var numbers = new HashSet<int>();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.All(char.IsAsciiDigit) && int.TryParse(token, out var n)) numbers.Add(n);
            else if (NumberWords.TryGetValue(token, out var word)) numbers.Add(word);
            else if (RomanNumerals.TryGetValue(token, out var roman)) numbers.Add(roman);
            else if (i > 0 && NumberLeads.Contains(tokens[i - 1]) && SingleLetterNumerals.TryGetValue(token, out var letter))
                numbers.Add(letter);
        }

        return numbers;
    }

    private static double TokenSortRatio(string a, string b) => Ratio(SortWords(a), SortWords(b));

    private static string SortWords(string normalized) =>
        string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal));

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static double Ratio(string a, string b)
    {
        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 1.0 : 1.0 - (double)Levenshtein(a, b) / longest;
    }

    /// <summary>
    /// Token-set ratio: split off the shared words, then compare "shared", "shared + rest of a" and
    /// "shared + rest of b" and keep the best pair. Words are sorted, so order does not matter.
    /// </summary>
    private static double TokenSetRatio(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        var shared = Join(ta.Intersect(tb));
        var restA = Join(ta.Except(tb));
        var restB = Join(tb.Except(ta));

        var withA = Combine(shared, restA);
        var withB = Combine(shared, restB);

        var best = Ratio(withA, withB);
        if (shared.Length > 0)
        {
            best = Math.Max(best, Math.Max(Ratio(shared, withA), Ratio(shared, withB)));
        }

        return best;
    }

    private static string Join(IEnumerable<string> tokens) =>
        string.Join(' ', tokens.OrderBy(t => t, StringComparer.Ordinal));

    private static string Combine(string shared, string rest) =>
        shared.Length == 0 ? rest : rest.Length == 0 ? shared : shared + " " + rest;
}
