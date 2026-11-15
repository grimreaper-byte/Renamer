using System.Globalization;
using System.Text;

namespace Renamer.Core;

public static class TitleMatch
{
    /// <summary>Lower case, no accents or punctuation, "&amp;" as "and", no leading "the".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Dropping apostrophes and dots keeps "Marvel's" = "Marvels" and "S.W.A.T." = "SWAT".
            if (c is '\'' or '’' or '.')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[0] == "the")
        {
            words = words[1..];
        }

        return string.Join(' ', words);
    }

    /// <summary>1 for the same title, falling towards 0 as the edit distance grows.</summary>
    public static double Similarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left == right)
        {
            return 1;
        }

        return 1.0 - (double)Levenshtein(left, right) / Math.Max(left.Length, right.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
