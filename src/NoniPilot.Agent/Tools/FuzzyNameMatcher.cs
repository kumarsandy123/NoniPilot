namespace NoniPilot.Agent.Tools;

/// <summary>
/// Tolerant name matching for voice-transcribed item names, which are often phonetically
/// close but not exactly spelled right (e.g. "amber gaiety" for a folder actually named
/// "AMBERG IT"). Normalizes away spaces/punctuation/case, then scores by exact/containment
/// match first, falling back to edit-distance similarity. Simple and cheap by design - not a
/// substitute for a real fuzzy-matching library, but sufficient for the typically-small
/// folder listings this is used against (see filesystem_open_best_match in ToolCatalog).
/// </summary>
public static class FuzzyNameMatcher
{
    /// <summary>
    /// Deliberately forgiving (0.4 similarity is not a strict bar) - voice transcription
    /// errors can be substantial, and acting on a plausible-but-imperfect guess is much
    /// better UX than refusing outright when there are only a handful of real candidates.
    /// </summary>
    private const double MinimumAcceptableScore = 0.4;

    public static T? FindBestMatch<T>(IReadOnlyList<T> items, string hint, Func<T, string> nameSelector)
        where T : class
    {
        var normalizedHint = Normalize(hint);
        if (normalizedHint.Length == 0 || items.Count == 0)
        {
            return null;
        }

        T? best = null;
        var bestScore = 0.0;

        foreach (var item in items)
        {
            var normalizedName = Normalize(nameSelector(item));
            if (normalizedName.Length == 0)
            {
                continue;
            }

            var score = Score(normalizedName, normalizedHint);
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return bestScore >= MinimumAcceptableScore ? best : null;
    }

    private static double Score(string normalizedName, string normalizedHint)
    {
        if (normalizedName == normalizedHint)
        {
            return 1.0;
        }

        if (normalizedName.Contains(normalizedHint) || normalizedHint.Contains(normalizedName))
        {
            return 0.85;
        }

        var distance = LevenshteinDistance(normalizedName, normalizedHint);
        var maxLen = Math.Max(normalizedName.Length, normalizedHint.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
