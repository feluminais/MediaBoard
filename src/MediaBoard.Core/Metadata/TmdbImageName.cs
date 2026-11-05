using System.Text.RegularExpressions;

namespace MediaBoard.Core.Metadata;

/// <summary>The file names TMDB uses for images. Both ends of the API check a name against this before it goes into a route or a file name.</summary>
public static partial class TmdbImageName
{
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    // \z, not $: a trailing newline would pass $.
    [GeneratedRegex(@"^[A-Za-z0-9]+\.(?:jpg|png)\z", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
