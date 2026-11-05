using System.Text.RegularExpressions;

namespace MediaBoard.Core.Metadata;

/// <summary>The file names TMDB uses for images. Both ends of the API check a name against this before it goes into a route or a file name.</summary>
public static partial class TmdbImageName
{
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    // \z, not $: a trailing newline would pass $. The length is far past any name TMDB hands out, and a name with no
    // limit lets a viewer fill the failed-names memory and the owner's outgoing requests.
    [GeneratedRegex(@"^[A-Za-z0-9]{1,60}\.(?:jpg|png)\z", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
