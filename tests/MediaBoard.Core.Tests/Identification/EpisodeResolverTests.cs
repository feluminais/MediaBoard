using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Identification;

public class EpisodeResolverTests
{
    private static ParsedMedia Parsed(int? season, int[] episodes, params string[] titles) =>
        new(MediaKind.Episode, "Show", null, season, episodes, titles, null, null, null);

    private static SeasonDetails Season(int season, params string[] names) =>
        new(season, names.Select((name, i) => new EpisodeDetails(season, i + 1, name, 22, null)).ToList());

    private static SeasonDetails SeasonWith(int season, params (int Number, string Name)[] episodes) =>
        new(season, episodes.Select(e => new EpisodeDetails(season, e.Number, e.Name, 11, null)).ToList());

    private static int[] Numbers(IReadOnlyList<EpisodeDetails>? result) =>
        result!.Select(e => e.Number).ToArray();

    [Fact]
    public void NumberMatch_NoTitle_ReturnsByNumber()
    {
        var result = EpisodeResolver.Resolve(Parsed(1, [2]), Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([2], Numbers(result));
        Assert.Equal("Second", result![0].Name);
    }

    [Fact]
    public void NumberAndTitleAgree_Returns()
    {
        var result = EpisodeResolver.Resolve(Parsed(1, [2], "Second"), Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([2], Numbers(result));
    }

    [Fact]
    public void TitleWinsOverNumber()
    {
        var season = SeasonWith(5, (1, "Opening"), (2, "Another"), (3, "Filler"), (4, "More"),
            (5, "Rise and Shine"), (6, "Waiting"), (7, "Fungus Among Us"));
        var parsed = Parsed(5, [3], "Rise and Shine & Waiting & Fungus Among Us");

        var result = EpisodeResolver.Resolve(parsed, season, null);

        Assert.Equal([5, 6, 7], Numbers(result));
    }

    [Fact]
    public void TitleWinsOverNumber_SingleTitleDisagreeingWithNumber()
    {
        var result = EpisodeResolver.Resolve(Parsed(1, [1], "Third"), Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([3], Numbers(result));
    }

    [Fact]
    public void RangeMarker_ReturnsTwo()
    {
        var result = EpisodeResolver.Resolve(Parsed(1, [1, 2]), Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([1, 2], Numbers(result));
    }


    [Fact]
    public void RangeWithSingleTitle_ReturnsWholeRange()
    {
        var result = EpisodeResolver.Resolve(Parsed(1, [1, 2], "Pilot"), Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([1, 2], Numbers(result));
    }

    [Fact]
    public void RangeWithTitleOutsideRange_TitleWins()
    {
        var season = Season(1, "Pilot", "Second", "Third", "Fourth", "Fifth");

        var result = EpisodeResolver.Resolve(Parsed(1, [1, 2], "Fifth"), season, null);

        Assert.Equal([5], Numbers(result));
    }
    [Fact]
    public void RangeMarker_AnyNumberMissing_ReturnsNull()
    {
        Assert.Null(EpisodeResolver.Resolve(Parsed(1, [3, 4]), Season(1, "Pilot", "Second", "Third"), null));
    }

    [Fact]
    public void TitleOnly_MatchesSeasonZeroSpecial()
    {
        var specials = SeasonWith(0, (1, "Behind the Scenes"), (2, "Christmas Special"));

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Christmas Special"), Season(1, "Pilot", "Second"), specials);

        Assert.Equal([2], Numbers(result));
        Assert.Equal(0, result![0].Season);
    }

    [Fact]
    public void SeasonTitle_BeatsSpecialsWithSameTitle()
    {
        var specials = SeasonWith(0, (1, "Second"));

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Second"), Season(1, "Pilot", "Second"), specials);

        Assert.Equal(1, result![0].Season);
    }

    [Fact]
    public void NothingFits_ReturnsNull()
    {
        Assert.Null(EpisodeResolver.Resolve(Parsed(1, [9], "Unrelated Words Here"), Season(1, "Pilot", "Second"), null));
    }

    [Fact]
    public void NoNumberNoTitle_ReturnsNull()
    {
        Assert.Null(EpisodeResolver.Resolve(Parsed(1, []), Season(1, "Pilot", "Second"), null));
    }

    [Fact]
    public void AmpersandInsideRealTitle()
    {
        var season = SeasonWith(1, (1, "Rock & Roll"), (2, "Rock"), (3, "Roll"));

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Rock & Roll"), season, null);

        Assert.Equal([1], Numbers(result));
    }

    [Fact]
    public void Split_PartsMustAllMatch_ElseFallsBackToNumber()
    {
        var season = Season(1, "Rise and Shine", "Waiting", "Fungus Among Us");

        var result = EpisodeResolver.Resolve(Parsed(1, [2], "Rise and Shine & Missing Piece"), season, null);

        Assert.Equal([2], Numbers(result));
    }

    [Fact]
    public void Split_PartsMapToDistinctEpisodes()
    {
        var season = Season(1, "Waiting", "Fungus Among Us");

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Waiting & Waiting"), season, null);

        Assert.Null(result);
    }

    [Fact]
    public void Split_ReturnsOrderedByNumber()
    {
        var season = Season(1, "Rise and Shine", "Waiting", "Fungus Among Us");

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Fungus Among Us & Rise and Shine"), season, null);

        Assert.Equal([1, 3], Numbers(result));
    }

    [Fact]
    public void WholeTitle_TieBetweenEpisodes_PrefersParsedNumber()
    {
        var season = Season(1, "Home", "Away", "Home");

        var result = EpisodeResolver.Resolve(Parsed(1, [3], "Home"), season, null);

        Assert.Equal([3], Numbers(result));
    }

    [Fact]
    public void WholeTitle_TieWithoutMatchingNumber_ReturnsNull()
    {
        var season = Season(1, "Home", "Away", "Home");

        Assert.Null(EpisodeResolver.Resolve(Parsed(1, [2], "Home"), season, null));
    }

    [Fact]
    public void CyrillicTitle_Matches()
    {
        var season = Season(1, "Початок", "Продовження");

        var result = EpisodeResolver.Resolve(Parsed(1, [], "Продовження"), season, null);

        Assert.Equal([2], Numbers(result));
    }

    [Fact]
    public void TwoHalfEpisodeFiles_ResolveByTitleWithoutError()
    {
        var season = SeasonWith(8, (15, "Sold!"), (16, "Gone!"));
        var a = Parsed(8, [15], "Sold!");
        var b = Parsed(8, [15], "Gone!");

        Assert.Equal([15], Numbers(EpisodeResolver.Resolve(a, season, null)));
        Assert.Equal([16], Numbers(EpisodeResolver.Resolve(b, season, null)));
    }

    [Fact]
    public void AcrossSeasons_UniqueTitleMatch_IsFound()
    {
        var result = EpisodeResolver.ResolveAcrossSeasons(
            Parsed(4, [13], "Bend Her"),
            [Season(3, "Amazon Women in the Mood"), Season(5, "Crimes of the Hot", "Bend Her", "Obsoletely Fabulous")]);

        var episode = Assert.Single(result!);
        Assert.Equal((5, 2), (episode.Season, episode.Number));
    }

    [Fact]
    public void AcrossSeasons_TitleInTwoSeasons_IsAmbiguous()
    {
        var result = EpisodeResolver.ResolveAcrossSeasons(
            Parsed(4, [13], "Bend Her"), [Season(3, "Bend Her"), Season(5, "Bend Her")]);

        Assert.Null(result);
    }

    [Fact]
    public void AcrossSeasons_NeedsANearExactTitle()
    {
        // "Godfella" and "Godfellas" score about 0.89: enough inside the file's own season, not across seasons.
        var result = EpisodeResolver.ResolveAcrossSeasons(Parsed(4, [13], "Godfella"), [Season(5, "Godfellas")]);

        Assert.Null(result);
    }

    [Fact]
    public void AcrossSeasons_NoTitle_FindsNothing()
    {
        Assert.Null(EpisodeResolver.ResolveAcrossSeasons(Parsed(4, [13]), [Season(5, "Bend Her")]));
    }
}
