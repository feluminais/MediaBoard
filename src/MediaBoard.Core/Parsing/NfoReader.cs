using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MediaBoard.Core.Parsing;

public static partial class NfoReader
{
    [GeneratedRegex(@"^tt\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbShape();

    /// <summary>
    /// Reads the TMDB and IMDb ids from a Kodi-style nfo: <c>&lt;tmdbid&gt;</c>, <c>&lt;imdbid&gt;</c> and
    /// <c>&lt;uniqueid type="tmdb|imdb"&gt;</c>. A missing, unreadable or malformed file gives nulls.
    /// </summary>
    public static (int? TmdbId, string? ImdbId) ReadIds(string nfoPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(nfoPath);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        var tmdb = Values(doc, "tmdbid").Concat(UniqueIds(doc, "tmdb"))
            .Select(v => int.TryParse(v, out var id) && id > 0 ? id : (int?)null)
            .FirstOrDefault(id => id is not null);

        var imdb = Values(doc, "imdbid").Concat(UniqueIds(doc, "imdb"))
            .Where(v => ImdbShape().IsMatch(v))
            .Select(v => v.ToLowerInvariant())
            .FirstOrDefault();

        return (tmdb, imdb);
    }

    private static IEnumerable<string> Values(XDocument doc, string name) =>
        doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim());

    private static IEnumerable<string> UniqueIds(XDocument doc, string type) =>
        doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "uniqueid", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim());
}
