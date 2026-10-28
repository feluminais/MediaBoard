using System.Globalization;
using System.Text;

namespace MediaBoard.Core.Identification;

public static class TitleNormalizer
{
    /// <summary>
    /// Lowercases, folds Latin diacritics (é to e), turns "&amp;" into "and", drops apostrophes,
    /// treats every other non-letter, non-digit as a space, collapses whitespace and drops a leading "the ".
    /// Cyrillic and other scripts are kept as they are.
    /// </summary>
    public static string Normalize(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastBaseIsAscii = false;
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                // Only Latin accents are folded; a Cyrillic breve (й) or diaeresis (ё) is part of the letter.
                if (!lastBaseIsAscii) sb.Append(ch);
                continue;
            }

            if (ch == '&')
            {
                pendingSpace = true;
                AppendSeparated(sb, "and", ref pendingSpace);
                pendingSpace = true;
                lastBaseIsAscii = false;
            }
            else if (IsApostrophe(ch))
            {
                // Dropped, not a separator: "Ocean's" and "Oceans" should agree.
            }
            else if (char.IsLetterOrDigit(ch))
            {
                AppendSeparated(sb, char.ToLowerInvariant(ch).ToString(), ref pendingSpace);
                lastBaseIsAscii = char.IsAscii(ch);
            }
            else
            {
                pendingSpace = true;
                lastBaseIsAscii = false;
            }
        }

        var normalized = sb.ToString().Normalize(NormalizationForm.FormC);
        return normalized.StartsWith("the ", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }

    private static bool IsApostrophe(char ch) => ch is '\'' or '’' or '‘' or '`' or 'ʼ';

    private static void AppendSeparated(StringBuilder sb, string text, ref bool pendingSpace)
    {
        if (pendingSpace && sb.Length > 0) sb.Append(' ');
        pendingSpace = false;
        sb.Append(text);
    }
}
