using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Parsing;

public class PathParserTests
{
    private const string SpongeBob = "SpongeBob SquarePants - DVDRip-AVC, SATRip-AVC [Ukr, Eng] [Hurtom]";

    private static readonly string[] GardenWall =
        [.. Enumerable.Range(1, 10).Select(i => $"[FanVoxUA]_Over_the_garden_wall_{i:00}_[1080p].mp4")];

    private static object?[] Movie(
        string path, string title, int? year = null, int? tmdbId = null, string? imdbId = null) =>
        [path, Array.Empty<string>(), MediaKind.Movie, title, year, null, Array.Empty<int>(), null, tmdbId, imdbId];

    private static object?[] Episode(
        string path, string title, int? year, int season, int[] episodes, string? showFolder,
        string[]? siblings = null, int? tmdbId = null, string? imdbId = null) =>
        [path, siblings ?? [], MediaKind.Episode, title, year, season, episodes, showFolder, tmdbId, imdbId];

    public static IEnumerable<object?[]> Rows =>
    [
        Movie("Bridge of Spies (2015) BDRemux 1080p.mkv", "Bridge of Spies", 2015),
        Episode(
            @"Better Call Saul 1080p H265\Season 01 (2015)\Better Call Saul.S01E01.Uno.mkv",
            "Better Call Saul", null, 1, [1], "Better Call Saul 1080p H265"),
        Episode(
            @"Brassic - Season 1 (2019) WEBRip 1080p [Ukr.Eng] [Цікава Ідея]\Brassic - S01E01 (2019) WEBRip 1080p [Ukr.Eng] [Цікава Ідея].mkv",
            "Brassic", 2019, 1, [1], "Brassic - Season 1 (2019) WEBRip 1080p [Ukr.Eng] [Цікава Ідея]"),
        Episode(
            @"Futurama [WEB-DL1080p]\Season 04\S04E01 Kif Gets Knocked Up a Notch [AMZN]WEB-DL1080p.mkv",
            "Futurama", null, 4, [1], "Futurama [WEB-DL1080p]"),
        Episode(
            @"South Park (1997) BDRip-AVC [UKR_ENG] [Hurtom]\Season 01\E02. Weight Gain 4000 (1997) BDRip-AVC [2xUKR_ENG] [Hurtom].mkv",
            "South Park", 1997, 1, [2], "South Park (1997) BDRip-AVC [UKR_ENG] [Hurtom]"),
        Movie(
            @"South Park (1997) BDRip-AVC [UKR_ENG] [Hurtom]\01. South Park Bigger, Longer & Uncut (1999) BDRip-AVC [2xUKR_ENG] [Hurtom].mkv",
            "South Park Bigger, Longer & Uncut", 1999),
        Episode(
            SpongeBob + @"\Season 05 (2007-2009) SATRip-AVC [Ukr] [Hurtom]\[5x03] Rise and Shine & Waiting & Fungus Among Us [Ukr] SATRip-AVC.mkv",
            "SpongeBob SquarePants", null, 5, [3], SpongeBob),
        Episode(
            SpongeBob + @"\Season 06 (2008-2010) DVDRip-AVC, SATRip-AVC [Ukr, Eng] [Hurtom]\[6x23-24] Truth or Square [Ukr.Eng] DVDRip-AVC.mkv",
            "SpongeBob SquarePants", null, 6, [23, 24], SpongeBob),
        Movie(
            SpongeBob + @"\The SpongeBob SquarePants Movie (2004) BDRip-AVC [2xUkr, Eng] [Hurtom].mkv",
            "The SpongeBob SquarePants Movie", 2004),
        Episode(
            @"Supernatural\Supernatural.S01.BluRay.1080p-DVT\Supernatural.S01E01.BluRay.1080p-DVT.mkv",
            "Supernatural", null, 1, [1], "Supernatural"),
        Episode(
            @"The Sopranos (1999) Season 1-6 S01-S06 (1080p BluRay x265)\Season 1\The Sopranos (1999) - S01E01 - The Sopranos (1080p BluRay x265 ImE).mkv",
            "The Sopranos", 1999, 1, [1], "The Sopranos (1999) Season 1-6 S01-S06 (1080p BluRay x265)"),
        Episode(
            "[FanVoxUA]_Over_the_garden_wall_01_[1080p].mp4",
            "Over the garden wall", null, 1, [1], null, GardenWall),
        Movie(@"Show\Movie Title (2010) [tmdbid-123].mkv", "Movie Title", 2010, tmdbId: 123),

        // Own cases beyond the brief.
        Movie("Toy Story 3.mkv", "Toy Story 3"),
        Movie(@"Inception (2010)\Inception.mkv", "Inception", 2010),
        Movie(@"Film (2010) [imdbid-tt0106145]\film.mkv", "film", 2010, imdbId: "tt0106145"),
        Movie("Cloudy.With.A.Chance.Of.Meatballs.2009.1080p.mkv", "Cloudy With A Chance Of Meatballs", 2009),
        Episode(
            @"Breaking Bad [tmdbid-1396]\Season 2\S02E03 Bit by a Dead Bee.mkv",
            "Breaking Bad", null, 2, [3], "Breaking Bad [tmdbid-1396]", tmdbId: 1396),
        Episode(@"Show\Show.S01E01-E02.720p.mkv", "Show", null, 1, [1, 2], "Show"),
        Episode(
            "[Group] Show - 05.mkv", "Show", null, 1, [5], null,
            ["[Group] Show - 04.mkv", "[Group] Show - 05.mkv", "[Group] Show - 06.mkv", "[Group] Show - 05.nfo"]),
    ];

    [Theory]
    [MemberData(nameof(Rows))]
    public void Parse_UsesFolders(
        string path, string[] siblings, MediaKind kind, string title, int? year, int? season,
        int[] episodes, string? showFolder, int? tmdbId, string? imdbId)
    {
        var parsed = PathParser.Parse(path, siblings);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episodes, parsed.Episodes);
        Assert.Equal(showFolder, parsed.ShowFolder);
        Assert.Equal(tmdbId, parsed.TmdbId);
        Assert.Equal(imdbId, parsed.ImdbId);
    }

    [Fact]
    public void Parse_KeepsEpisodeTitles()
    {
        var parsed = PathParser.Parse(@"Better Call Saul\Season 01\Better Call Saul.S01E01.Uno.mkv", []);

        Assert.Equal(["Uno"], parsed.EpisodeTitles);
    }

    [Fact]
    public void Parse_AcceptsForwardSlashes()
    {
        var parsed = PathParser.Parse("Futurama/Season 04/S04E01 Kif Gets Knocked Up a Notch.mkv", []);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal("Futurama", parsed.Title);
        Assert.Equal("Futurama", parsed.ShowFolder);
    }

    [Fact]
    public void Parse_BareNumberNeedsTwoSiblingsWithTheSameTitle()
    {
        var parsed = PathParser.Parse(
            "[Group] Show - 05.mkv", ["[Group] Show - 05.mkv", "[Group] Show - 06.mkv"]);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Empty(parsed.Episodes);
    }

    [Fact]
    public void Parse_BareNumberWithYearIsNeverAnEpisode()
    {
        string[] siblings = ["Movie 1 (2001).mkv", "Movie 2 (2003).mkv", "Movie 3 (2005).mkv"];

        var parsed = PathParser.Parse("Movie 2 (2003).mkv", siblings);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Equal("Movie 2", parsed.Title);
    }
}
