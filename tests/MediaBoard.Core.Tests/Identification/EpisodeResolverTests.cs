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

    private static (int Season, int Number)[] Keys(IReadOnlyList<EpisodeDetails>? result) =>
        result!.Select(e => (e.Season, e.Number)).ToArray();

    /// <summary><see cref="EpisodeResolver.ResolveAsync"/> over a show that has exactly these seasons.</summary>
    private static Task<IReadOnlyList<EpisodeDetails>?> ResolveAsync(ParsedMedia parsed, params SeasonDetails[] seasons)
    {
        var byNumber = seasons.ToDictionary(s => s.Season);
        return EpisodeResolver.ResolveAsync(
            parsed,
            number => Task.FromResult(byNumber.GetValueOrDefault(number)),
            () => Task.FromResult<IReadOnlyList<int>>([.. byNumber.Keys.Order()]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Second")]
    public void NumberMatch_ReturnsByNumber_WithNoTitleOrAnAgreeingOne(string? title)
    {
        var parsed = title is null ? Parsed(1, [2]) : Parsed(1, [2], title);

        var result = EpisodeResolver.Resolve(parsed, Season(1, "Pilot", "Second", "Third"), null);

        Assert.Equal([2], Numbers(result));
        Assert.Equal("Second", result![0].Name);
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

    [Theory]
    [InlineData("Kif Gets Knocked Up a Notch", true)]
    [InlineData("Roswell That Ends Well", false)]
    public void TitleDisagrees_WhenTheTitleIsFarFromTheEpisodeAtTheNumber(string title, bool expected)
    {
        var episodes = Season(4, "Roswell That Ends Well").Episodes;

        Assert.Equal(expected, EpisodeResolver.TitleDisagrees(Parsed(4, [1], title), episodes));
    }

    [Theory]
    [InlineData("Pilot", false)]
    [InlineData("Something Else Entirely", true)]
    public void TitleDisagrees_MultiEpisode_AgreesWhenAnyEpisodeFits(string title, bool expected)
    {
        Assert.Equal(expected, EpisodeResolver.TitleDisagrees(Parsed(1, [1, 2], title), Season(1, "Pilot", "Two").Episodes));
    }

    [Fact]
    public void TitleDisagrees_SegmentTitle_AgreesWhenOneSegmentFits()
    {
        var episodes = SeasonWith(6, (44, "No Hat for Pat")).Episodes;

        Assert.False(EpisodeResolver.TitleDisagrees(Parsed(6, [44], "No Hat For Pat & Toy Store of Doom"), episodes));
    }

    [Fact]
    public void TitleDisagrees_TheShowsOwnName_IsNoEpisodeTitle()
    {
        // "S24E01. South Park (2020)": the name after the marker is the show's, not the episode's.
        Assert.False(EpisodeResolver.TitleDisagrees(Parsed(24, [1], "Show"), Season(24, "The Pandemic Special").Episodes));
    }

    // ---- Segments ("A & B"): each links its own episode ----

    [Fact]
    public async Task Segments_EachLinksItsOwnEpisode_InTheSeasonFirst_ThenInOtherSeasons()
    {
        var four = SeasonWith(4, (36, "Rise and Shine"), (37, "Waiting"));
        var five = SeasonWith(5, (3, "Boat Smarts"), (27, "Fungus Among Us"));

        var result = await ResolveAsync(Parsed(5, [3], "Rise and Shine & Waiting & Fungus Among Us"), four, five);

        Assert.Equal([(4, 36), (4, 37), (5, 27)], Keys(result));
    }

    [Fact]
    public async Task Segments_ASegmentThatFindsNothing_IsSkipped()
    {
        var five = SeasonWith(5, (8, "Krabs a la Mode"), (15, "Money Talks"), (17, "Slimy Dancing"));

        var result = await ResolveAsync(Parsed(5, [8], "Money Talks & Nothing Like It & Slimy Dancing"), five);

        Assert.Equal([(5, 15), (5, 17)], Keys(result));
    }

    [Fact]
    public async Task Segments_TitleAgreeingWithTheNumber_KeepsIt()
    {
        var six = SeasonWith(6, (1, "Krabby Road"));
        var seven = SeasonWith(7, (1, "Tentacle-Vision"), (2, "Growth Spout"));

        var result = await ResolveAsync(Parsed(7, [1], "Tentacle-Vision & Krabby Road"), six, seven);

        Assert.Equal([(7, 1)], Keys(result));
    }

    [Fact]
    public async Task Segments_CommaInATitleThatAgrees_IsNoSplit()
    {
        var one = SeasonWith(1, (1, "Hello"), (2, "Goodbye"));
        var two = SeasonWith(2, (3, "Hello, Goodbye, Amen"));

        var result = await ResolveAsync(Parsed(2, [3], "Hello, Goodbye"), one, two);

        Assert.Equal([(2, 3)], Keys(result));
    }

    [Fact]
    public async Task Segments_AmpersandInATitleThatAgrees_IsNoSplit()
    {
        var one = SeasonWith(1, (2, "Fire"));
        var three = SeasonWith(3, (7, "Fire & Ice"));

        var result = await ResolveAsync(Parsed(3, [7], "Fire & Ice (Extended Cut)"), one, three);

        Assert.Equal([(3, 7)], Keys(result));
    }

    [Fact]
    public async Task Segments_OneWordSegment_LinksOnlyAnExactName()
    {
        // "Mooncations" scores about 0.91 against "Mooncation": close enough for a longer name, not for one word.
        var one = SeasonWith(1, (1, "Mooncation"), (2, "Goodbye Again My Friend"), (3, "Third Thing"));

        var result = await ResolveAsync(Parsed(1, [3], "Mooncations & Goodbye Again My Friend"), one);

        Assert.Equal([(1, 2)], Keys(result));
    }

    [Fact]
    public async Task Segments_FoundInTwoSeasons_IsSkipped()
    {
        var four = SeasonWith(4, (1, "Waiting"));
        var five = SeasonWith(5, (3, "Boat Smarts"), (27, "Fungus Among Us"));
        var six = SeasonWith(6, (1, "Waiting"));

        var result = await ResolveAsync(Parsed(5, [3], "Waiting & Fungus Among Us"), four, five, six);

        Assert.Equal([(5, 27)], Keys(result));
    }

    [Fact]
    public async Task Segments_CommaSeparatedEpisodeNames_AreSegmentsToo()
    {
        var seven = SeasonWith(7, (9, "Truth or Square"), (16, "Back to the Past"), (17, "The Bad Guy Club for Villains"));

        var result = await ResolveAsync(Parsed(7, [9], "Back to the Past, The Bad Guy Club for Villains"), seven);

        Assert.Equal([(7, 16), (7, 17)], Keys(result));
    }

    [Fact]
    public async Task Segments_CommaWhereOnePieceIsNoEpisode_IsNotASplit()
    {
        var one = SeasonWith(1, (1, "Pilot"), (2, "Goodbye"));

        var result = await ResolveAsync(Parsed(1, [1], "Hello, Goodbye"), one);

        Assert.Empty(result!);
    }

    // ---- Parts ("Story. Part 2") ----

    private static readonly SeasonDetails Imaginationland = SeasonWith(11,
        (5, "Something Else"), (10, "Imaginationland"), (11, "Imaginationland, Episode II"), (12, "Imaginationland, Episode III"));

    [Theory]
    [InlineData("Imaginationland. Part 1", 10, 10)]
    [InlineData("Imaginationland. Part 2", 11, 11)]
    [InlineData("Imaginationland Part 3", 12, 12)]
    [InlineData("Imaginationland - Part II", 11, 11)]
    [InlineData("Imaginationland. Part 3", 5, 12)]
    [InlineData("Imaginationland. Part 1", 5, 10)]
    public async Task PartTitle_LinksTheEpisodeWithTheSamePartNumber(string title, int number, int expected)
    {
        var result = await ResolveAsync(Parsed(11, [number], title), Imaginationland);

        Assert.Equal([(11, expected)], Keys(result));
    }

    [Theory]
    [InlineData("Cartoon Wars (2)")]
    [InlineData("Cartoon Wars Part 2")]
    [InlineData("Cartoon Wars: Part Two")]
    [InlineData("Cartoon Wars, Episode II")]
    public async Task PartTitle_FindsThePartInAnyForm_InAnotherSeason(string tmdbName)
    {
        var ten = SeasonWith(10, (1, "Unrelated"), (4, tmdbName));
        var nine = SeasonWith(9, (4, "Something Else"));

        var result = await ResolveAsync(Parsed(9, [4], "Cartoon Wars. Part 2"), nine, ten);

        Assert.Equal([(10, 4)], Keys(result));
    }

    [Theory]
    [InlineData("Bender's Big Score. Part 1")]
    [InlineData("Bender's Big Score Part 4")]
    [InlineData("Bender's Big Score - Part II")]
    public async Task PartTitle_OfAFilmTmdbListsWhole_LinksNoEpisode(string title)
    {
        var specials = SeasonWith(0, (1, "Bender's Big Score"), (5, "Bender's Game"));
        var five = SeasonWith(5, (1, "Crimes of the Hot"), (4, "A Taste of Freedom"));

        var result = await ResolveAsync(Parsed(5, [1], title), specials, five);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task PartTitle_SeasonTmdbLacks_FilmListedWhole_IsTheShowWithNoEpisode()
    {
        var specials = SeasonWith(0, (5, "Bender's Game"));
        var five = SeasonWith(5, (1, "Crimes of the Hot"));

        var result = await ResolveAsync(Parsed(9, [2], "Bender's Game. Part 2"), specials, five);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task PartTitle_WithACommaInTheName_IsStillAPart()
    {
        var one = SeasonWith(1, (1, "Pilot"));
        var two = SeasonWith(2, (3, "Hello, Dolly (2)"));

        var result = await ResolveAsync(Parsed(1, [1], "Hello, Dolly. Part 2"), one, two);

        Assert.Equal([(2, 3)], Keys(result));
    }

    [Theory]
    [InlineData("Bender's Big Score. Part 1", "Bender's Big Score", 1)]
    [InlineData("Into the Wild Green Yonder Part IV", "Into the Wild Green Yonder", 4)]
    [InlineData("Imaginationland - part two", "Imaginationland", 2)]
    [InlineData("Counterpart", null, 0)]
    [InlineData("Part 2", null, 0)]
    [InlineData("Cartoon Wars (1)", null, 0)]
    public void PartOf_ReadsATrailingPartNumber(string title, string? whole, int number)
    {
        var part = EpisodeResolver.PartOf(title);

        Assert.Equal(whole, part?.Whole);
        if (whole is not null) Assert.Equal(number, part!.Value.Number);
    }

    [Fact]
    public async Task PartOnBothSides_StaysOnThatPart()
    {
        var ten = SeasonWith(10, (3, "Cartoon Wars (1)"), (4, "Cartoon Wars (2)"));

        var result = await ResolveAsync(Parsed(10, [4], "Cartoon Wars. Part 2"), ten);

        Assert.Equal([(10, 4)], Keys(result));
    }

    // ---- No silent fallback to the number ----

    [Fact]
    public async Task TitleNamingNoEpisode_LinksNoEpisode_NotTheOneAtItsNumber()
    {
        var six = SeasonWith(6, (11, "A Life in a Day"), (37, "SpongeBob SquarePants vs. The Big One"));

        var result = await ResolveAsync(Parsed(6, [11], "SpongeBob SquarePants and the big wave"), six);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task TheShowsOwnNameAsTitle_KeepsTheNumber()
    {
        var result = await ResolveAsync(Parsed(24, [1], "Show"), SeasonWith(24, (1, "The Pandemic Special")));

        Assert.Equal([(24, 1)], Keys(result));
    }

    [Theory]
    [InlineData("Episode 5")]
    [InlineData("Episode #5")]
    [InlineData("TBA")]
    [InlineData("")]
    public async Task TmdbPlaceholderName_KeepsTheNumber(string placeholder)
    {
        var result = await ResolveAsync(Parsed(29, [5], "The Real Title"), SeasonWith(29, (4, "Before"), (5, placeholder)));

        Assert.Equal([(29, 5)], Keys(result));
    }

    [Fact]
    public async Task NonLatinTitle_KeepsTheNumber()
    {
        var result = await ResolveAsync(Parsed(1, [2], "Відвідувач"), SeasonWith(1, (1, "Pilot"), (2, "The Visitor")));

        Assert.Equal([(1, 2)], Keys(result));
    }

    [Fact]
    public async Task ShowNameWithAnAmpersand_IsNoSegmentedTitle()
    {
        var parsed = Parsed(1, [1], "Will & Grace") with { Title = "Will & Grace" };

        var result = await ResolveAsync(parsed, SeasonWith(1, (1, "Pilot")), SeasonWith(2, (7, "Grace"), (8, "Will")));

        Assert.Equal([(1, 1)], Keys(result));
    }
}
