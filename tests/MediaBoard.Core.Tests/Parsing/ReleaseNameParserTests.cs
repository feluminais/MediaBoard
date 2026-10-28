using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Parsing;

public class ReleaseNameParserTests
{
    private static readonly int[] None = [];

    private static object?[] Row(
        string input, string title, int? year = null, int? season = null,
        int[]? episodes = null, string[]? episodeTitles = null,
        int? leadingNumber = null, int? tmdbId = null, string? imdbId = null) =>
        [input, title, year, season, episodes ?? None, episodeTitles ?? [], leadingNumber, tmdbId, imdbId];

    public static IEnumerable<object?[]> Rows =>
    [
        Row("Bridge of Spies (2015) BDRemux 1080p", "Bridge of Spies", 2015),
        Row("Bullet Train (2022) BDRemux 1080p UkrEng Sub UkrEng [Hurtom]", "Bullet Train", 2022),
        Row("Captain America Brave New World (2025) BDRemux 2160p 2xUkr Eng [Hurtom]", "Captain America Brave New World", 2025),
        Row("Cloudy.With.A.Chance.Of.Meatballs.2009.1080p", "Cloudy With A Chance Of Meatballs", 2009),
        Row("Everything Everywhere All at Once (2022) WEB-DL 4k 2160p H.265 SDR [Ukr_Eng] [Hurtom]", "Everything Everywhere All at Once", 2022),
        Row("Klaus (2019) WEB-DL 2160p Dolby Vision HDR [Hybrid]", "Klaus", 2019),
        Row("The Gentlemen (2019) BDRip 1080p (Hurtom)", "The Gentlemen", 2019),
        Row("Spider-Man Across the Spider-Verse (2023)", "Spider-Man Across the Spider-Verse", 2023),
        Row("Better Call Saul.S01E01.Uno", "Better Call Saul", null, 1, [1], ["Uno"]),
        Row("Brassic - S01E01 (2019) WEBRip 1080p [Ukr.Eng] [Цікава Ідея]", "Brassic", 2019, 1, [1]),
        Row("S04E02 Leela’s Homeworld [AMZN]WEB-DL1080p", "", null, 4, [2], ["Leela’s Homeworld"]),
        Row("S01E01. MobLand (2025). Stick or Twist", "MobLand", 2025, 1, [1], ["Stick or Twist"]),
        Row("Narcos.Mexico.S02E03.WEBDL.1080p.H265.Toloka", "Narcos Mexico", null, 2, [3]),
        Row("The Sopranos (1999) - S01E01 - The Sopranos (1080p BluRay x265 ImE)", "The Sopranos", 1999, 1, [1], ["The Sopranos"]),
        Row("Widows.Bay.S01E01.2026.ATVP.WEB-DL.1080p", "Widows Bay", 2026, 1, [1]),
        Row("Supernatural.S01E01.BluRay.1080p-DVT", "Supernatural", null, 1, [1]),
        Row("Show.S01E01-E02.720p", "Show", null, 1, [1, 2]),
        Row("Show 1x02 Title", "Show", null, 1, [2], ["Title"]),
        Row("[5x03] Rise and Shine & Waiting & Fungus Among Us [Ukr] SATRip-AVC", "", null, 5, [3], ["Rise and Shine & Waiting & Fungus Among Us"]),
        Row("E02. Weight Gain 4000 (1997) BDRip-AVC [2xUKR_ENG] [Hurtom]", "", 1997, null, [2], ["Weight Gain 4000"]),
        Row("[FanVoxUA]_Over_the_garden_wall_01_[1080p]", "Over the garden wall", leadingNumber: 1),
        Row("01. South Park Bigger, Longer & Uncut (1999) BDRip-AVC [2xUKR_ENG] [Hurtom]", "South Park Bigger, Longer & Uncut", 1999, leadingNumber: 1),
        Row("Movie.2010.UHD.BluRay.2160p.x265", "Movie", 2010),
        Row("Movie.Name.2010.REMASTERED.1080p.BluRay", "Movie Name", 2010),
        Row("Movie.2010.DDP5.1.Atmos.1080p", "Movie", 2010),
        Row("The 1999 Incident (2015) 1080p", "The 1999 Incident", 2015),
        Row("The 1999 Incident 2015 1080p", "The 1999 Incident", 2015),
        Row("Film (2010) [tmdbid-65567]", "Film", 2010, tmdbId: 65567),
        Row("Film (2010) [imdbid-tt0106145]", "Film", 2010, imdbId: "tt0106145"),
    ];

    [Theory]
    [MemberData(nameof(Rows))]
    public void Parse_ExtractsFields(
        string input, string title, int? year, int? season,
        int[] episodes, string[] episodeTitles,
        int? leadingNumber, int? tmdbId, string? imdbId)
    {
        var parsed = ReleaseNameParser.Parse(input);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episodes, parsed.Episodes);
        Assert.Equal(episodeTitles, parsed.EpisodeTitles);
        Assert.Equal(leadingNumber, parsed.LeadingNumber);
        Assert.Equal(tmdbId, parsed.TmdbId);
        Assert.Equal(imdbId, parsed.ImdbId);
    }

    [Fact]
    public void Parse_KeepsUnicodeAndPipes()
    {
        Assert.Equal("Назва | Тест", ReleaseNameParser.Parse("Назва | Тест (2020)").Title);
    }
}
