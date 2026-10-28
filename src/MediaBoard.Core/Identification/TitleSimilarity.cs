namespace MediaBoard.Core.Identification;

public static class TitleSimilarity
{
    /// <summary>
    /// Similarity of two titles in 0..1: <see cref="ScoringWeights.TitleSortShare"/> times the token-sort
    /// ratio plus <see cref="ScoringWeights.TitleSetShare"/> times the token-set ratio. Both parts ignore
    /// word order. The token-sort ratio is the Levenshtein ratio of the normalized words sorted
    /// alphabetically, so extra words (a sequel number) cost points; the token-set ratio compares the
    /// shared words with the rest and scores a title that contains the other's words as 1.0.
    /// </summary>
    public static double Score(string a, string b) => ScoreNormalized(TitleNormalizer.Normalize(a), TitleNormalizer.Normalize(b));

    /// <summary>
    /// <see cref="Score"/> for two titles that went through <see cref="TitleNormalizer.Normalize"/> already, for
    /// callers that compare one title against many and normalize each only once.
    /// </summary>
    public static double ScoreNormalized(string na, string nb)
    {
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        if (na == nb) return 1.0;

        var score = ScoringWeights.TitleSortShare * TokenSortRatio(na, nb)
                    + ScoringWeights.TitleSetShare * TokenSetRatio(na, nb);
        return Math.Clamp(score, 0.0, 1.0);
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
