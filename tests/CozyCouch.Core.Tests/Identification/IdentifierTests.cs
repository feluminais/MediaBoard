using CozyCouch.Core.Identification;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Parsing;
using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Identification;

public class IdentifierTests : IDisposable
{
    private readonly FakeMetadataProvider _tmdb = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-id-" + Guid.NewGuid().ToString("N"));

    public IdentifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IdentificationResult> Identify(ParsedMedia parsed, string? path = null, double? durationS = null) =>
        new Identifier(_tmdb).IdentifyAsync(parsed, path ?? Path.Combine(_dir, "file.mkv"), durationS, Ct);

    private static ParsedMedia Episode(string title, int? year = null, int season = 1, params int[] episodes) =>
        new(MediaKind.Episode, title, year, season, episodes.Length == 0 ? [1] : episodes, [], title, null, null);

    private static ParsedMedia Movie(string title, int? year = null, string? showFolder = null) =>
        new(MediaKind.Movie, title, year, null, [], [], showFolder, null, null);

    private static SearchCandidate Show(int id, string name, int? year, double popularity = 50, int votes = 0) =>
        new(id, TitleKind.Show, name, null, year, popularity, 8.0, null, null, votes);

    private static SearchCandidate Film(int id, string name, int? year, double popularity = 50, int votes = 0) =>
        new(id, TitleKind.Movie, name, null, year, popularity, 7.0, null, null, votes);

    private static TitleDetails ShowTitle(int id, string name, int? year, params int[] seasons) =>
        new(id, TitleKind.Show, name, year, null, null, 8.0, null, 45, seasons);

    private static TitleDetails FilmTitle(int id, string name, int? year, int? runtimeMin = 120) =>
        new(id, TitleKind.Movie, name, year, null, null, 7.0, null, runtimeMin, []);

    private static SeasonDetails Season(int season, params string[] names) =>
        new(season, names.Select((name, i) => new EpisodeDetails(season, i + 1, name, 45, null)).ToList());

    private string WriteNfo(string relativePath, string xml)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public async Task TmdbIdTag_MatchesWithoutSearch()
    {
        _tmdb.OnTitle(FilmTitle(603, "The Matrix", 1999));
        var parsed = Movie("The Matrix", 1999) with { TmdbId = 603 };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(603, result.Title!.TmdbId);
        Assert.Equal("id_tag", result.Source);
        Assert.Equal(1.0, result.Confidence);
        Assert.Empty(_tmdb.Searches);
        Assert.Empty(_tmdb.ImdbLookups);
    }

    [Fact]
    public async Task TmdbIdTag_KindFollowsParsedKind_AndEpisodesResolve()
    {
        _tmdb.OnTitle(ShowTitle(1399, "Game of Thrones", 2011, 1))
            .OnSeason(1399, Season(1, "Winter Is Coming", "The Kingsroad"));
        var parsed = Episode("Game of Thrones", null, 1, 2) with { TmdbId = 1399 };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(TitleKind.Show, result.Title!.Kind);
        Assert.Equal([2], result.Episodes.Select(e => e.Number));
        Assert.Empty(_tmdb.Searches);
    }

    [Fact]
    public async Task ImdbIdTag_UsesFind()
    {
        _tmdb.OnImdb("tt0133093", 603, TitleKind.Movie).OnTitle(FilmTitle(603, "The Matrix", 1999));
        var parsed = Movie("The Matrix", 1999) with { ImdbId = "tt0133093" };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(603, result.Title!.TmdbId);
        Assert.Equal("id_tag", result.Source);
        Assert.Equal(["tt0133093"], _tmdb.ImdbLookups);
        Assert.Empty(_tmdb.Searches);
    }

    [Fact]
    public async Task ImdbIdTag_UnknownToTmdb_FallsBackToSearch()
    {
        _tmdb.OnSearch("The Matrix", 1999, TitleKind.Movie, Film(603, "The Matrix", 1999))
            .OnTitle(FilmTitle(603, "The Matrix", 1999));
        var parsed = Movie("The Matrix", 1999) with { ImdbId = "tt9999999" };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal("auto", result.Source);
    }

    [Fact]
    public async Task NfoId_IsUsed_FromFileNamedNfoNextToVideo()
    {
        WriteNfo(Path.Combine("Movies", "Heat.1995.nfo"), "<movie><tmdbid>949</tmdbid></movie>");
        _tmdb.OnTitle(FilmTitle(949, "Heat", 1995));

        var result = await Identify(Movie("Heat", 1995), Path.Combine(_dir, "Movies", "Heat.1995.mkv"));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(949, result.Title!.TmdbId);
        Assert.Equal("id_tag", result.Source);
        Assert.Empty(_tmdb.Searches);
    }

    [Fact]
    public async Task NfoId_IsUsed_FromTvshowNfoInShowFolder()
    {
        WriteNfo(Path.Combine("Shows", "Brassic", "tvshow.nfo"), "<tvshow><uniqueid type=\"tmdb\">91239</uniqueid></tvshow>");
        _tmdb.OnTitle(ShowTitle(91239, "Brassic", 2019, 1)).OnSeason(91239, Season(1, "Pilot", "Two"));

        var result = await Identify(Episode("Brassic", null, 1, 1), Path.Combine(_dir, "Shows", "Brassic", "Season 1", "ep1.mkv"));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(91239, result.Title!.TmdbId);
        Assert.Equal("id_tag", result.Source);
        Assert.Empty(_tmdb.Searches);
    }

    [Fact]
    public async Task NfoId_EpisodeNfoNextToFile_IsNotTakenAsTheShowId()
    {
        WriteNfo(Path.Combine("Shows", "Brassic", "ep1.nfo"), "<episodedetails><tmdbid>777</tmdbid></episodedetails>");
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 1)).OnSeason(91239, Season(1, "Pilot"));

        var result = await Identify(Episode("Brassic", 2019), Path.Combine(_dir, "Shows", "Brassic", "ep1.mkv"));

        Assert.Equal(91239, result.Title!.TmdbId);
        Assert.Equal("auto", result.Source);
    }

    [Fact]
    public async Task NameTag_WinsOverNfo()
    {
        WriteNfo("Heat.nfo", "<movie><tmdbid>111</tmdbid></movie>");
        _tmdb.OnTitle(FilmTitle(949, "Heat", 1995));

        var result = await Identify(Movie("Heat", 1995) with { TmdbId = 949 }, Path.Combine(_dir, "Heat.mkv"));

        Assert.Equal(949, result.Title!.TmdbId);
    }

    [Fact]
    public async Task ClearWinner_IsMatched()
    {
        _tmdb.OnSearch("The Matrix", 1999, TitleKind.Movie,
                Film(603, "The Matrix", 1999, 90), Film(604, "The Matrix Reloaded", 2003, 60))
            .OnTitle(FilmTitle(603, "The Matrix", 1999));

        var result = await Identify(Movie("The Matrix", 1999));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(603, result.Title!.TmdbId);
        Assert.Equal("auto", result.Source);
        Assert.InRange(result.Confidence, ScoringWeights.AcceptThreshold, 1.0);
        Assert.Empty(result.Episodes);
    }

    [Fact]
    public async Task CloseCall_GoesToReview_WithTopCandidates()
    {
        _tmdb.OnSearch("Dune", 2021, TitleKind.Movie,
            Film(1, "Dune", 2021, 80), Film(2, "Dune", 2021, 70), Film(3, "Dune", 2021, 60),
            Film(4, "Dune", 2021, 50), Film(5, "Dune", 2021, 40), Film(6, "Dune", 2021, 30), Film(7, "Dune", 2021, 20));

        var result = await Identify(Movie("Dune", 2021));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Null(result.Title);
        Assert.Equal(5, result.Candidates.Count);
        Assert.Equal([1, 2, 3, 4, 5], result.Candidates.Select(c => c.TmdbId));
        Assert.Equal("auto", result.Source);
    }

    [Fact]
    public async Task SameTitleNoYear_ClearlyMoreVotedShow_IsMatched()
    {
        KnowSupernatural(leaderVotes: 8789, runnerUpVotes: 11);

        var result = await Identify(Episode("Supernatural", null, 1, 1));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(1622, result.Title!.TmdbId);
    }

    [Theory]
    [InlineData(8789, 900)] // not ten times the runner-up's votes
    [InlineData(99, 5)] // too few votes to be sure
    public async Task SameTitleNoYear_LeaderWithoutAClearVoteLead_GoesToReview(int leaderVotes, int runnerUpVotes)
    {
        KnowSupernatural(leaderVotes, runnerUpVotes);

        var result = await Identify(Episode("Supernatural", null, 1, 1));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task SameTitle_KnownYear_WinsOverVotes()
    {
        _tmdb.OnSearch("Supernatural", 1977, TitleKind.Show,
                Show(1622, "Supernatural", 2005, 5025, votes: 8789), Show(13836, "Supernatural", 1977, 6, votes: 11))
            .OnTitle(ShowTitle(13836, "Supernatural", 1977, 1)).OnSeason(13836, Season(1, "Ghost Town"));

        var result = await Identify(Episode("Supernatural", 1977, 1, 1));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(13836, result.Title!.TmdbId);
    }

    [Fact]
    public async Task DifferentTitlesNoYear_VotesDoNotBreakTheCloseCall()
    {
        _tmdb.OnSearch("Spartacus", null, TitleKind.Movie,
            Film(1, "Spartacus", 1960, 50, votes: 5000), Film(2, "Spartakus", 1976, 50, votes: 10));

        var result = await Identify(Movie("Spartacus"));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
    }

    private void KnowSupernatural(int leaderVotes, int runnerUpVotes) =>
        _tmdb.OnSearch("Supernatural", null, TitleKind.Show,
                Show(1622, "Supernatural", 2005, 5025, leaderVotes), Show(13836, "Supernatural", 1977, 6, runnerUpVotes))
            .OnTitle(ShowTitle(1622, "Supernatural", 2005, 1)).OnSeason(1622, Season(1, "Pilot"));

    [Fact]
    public async Task LowScore_GoesToReview_AfterAllRetries()
    {
        _tmdb.OnSearch("Dune Part Two", 2024, TitleKind.Movie, Film(9, "Something Else Entirely", 1970));

        var result = await Identify(Movie("Dune Part Two", 2024));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal([9], result.Candidates.Select(c => c.TmdbId));
        Assert.True(result.Confidence < ScoringWeights.AcceptThreshold);
    }

    [Fact]
    public async Task NoResults_ReviewWithNoCandidates()
    {
        var result = await Identify(Movie("Nothing Like It", 2001));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task NoResults_RetriesShorterTitle_ThenWithoutYear()
    {
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 1)).OnSeason(91239, Season(1, "Pilot"));

        var result = await Identify(Episode("Brassic Season 1", 2019));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(91239, result.Title!.TmdbId);
        Assert.Equal(
            [("Brassic Season 1", (int?)2019), ("Brassic Season", 2019), ("Brassic", 2019)],
            _tmdb.Searches.Select(s => (s.Title, s.Year)));
    }

    [Fact]
    public async Task NoResults_RetriesWithoutYear_AfterShortTitlesWithYear()
    {
        _tmdb.OnSearch("Brassic Show", null, TitleKind.Show, Show(91239, "Brassic Show", 2019))
            .OnTitle(ShowTitle(91239, "Brassic Show", 2019, 1)).OnSeason(91239, Season(1, "Pilot"));

        var result = await Identify(Episode("Brassic Show", 2020));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(
            [("Brassic Show", (int?)2020), ("Brassic", 2020), ("Brassic Show", null)],
            _tmdb.Searches.Select(s => (s.Title, s.Year)));
    }

    [Fact]
    public async Task Search_StopsAtFirstAttemptThatAccepts()
    {
        _tmdb.OnSearch("The Matrix", 1999, TitleKind.Movie, Film(603, "The Matrix", 1999))
            .OnTitle(FilmTitle(603, "The Matrix", 1999));

        await Identify(Movie("The Matrix", 1999));

        Assert.Single(_tmdb.Searches);
    }

    [Fact]
    public async Task Review_CandidatesPoolAcrossAttempts_WithoutDuplicates()
    {
        _tmdb.OnSearch("Odd Name", 2000, TitleKind.Movie, Film(1, "Odd Thing", 1990))
            .OnSearch("Odd", 2000, TitleKind.Movie, Film(1, "Odd Thing", 1990), Film(2, "Odds", 1991));

        var result = await Identify(Movie("Odd Name", 2000));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal([1, 2], result.Candidates.Select(c => c.TmdbId).Order());
    }

    [Fact]
    public async Task EpisodeUnresolved_GoesToReview()
    {
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 1))
            .OnSeason(91239, Season(1, "Pilot", "Two"));

        var result = await Identify(Episode("Brassic", 2019, 1, 9));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Null(result.Title);
        Assert.Empty(result.Episodes);
        Assert.Equal(91239, result.Candidates[0].TmdbId);
    }

    [Fact]
    public async Task EpisodeAirYear_IsNeverSentAsTheFirstAirYear()
    {
        _tmdb.OnSearch("South Park", null, TitleKind.Show, Show(2190, "South Park", 1997))
            .OnTitle(ShowTitle(2190, "South Park", 1997, 24)).OnSeason(2190, Season(24, "The Pandemic Special"));

        var result = await Identify(Episode("South Park", null, 24, 1) with { AirYear = 2020 });

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(2190, result.Title!.TmdbId);
        Assert.Equal([("South Park", (int?)null)], _tmdb.Searches.Select(s => (s.Title, s.Year)));
    }

    [Fact]
    public async Task EpisodeShowYear_IsSearched_NotTheAirYear()
    {
        _tmdb.OnSearch("South Park", 1997, TitleKind.Show, Show(2190, "South Park", 1997, 150))
            .OnSearch("South Park", 2002, TitleKind.Show, Show(612, "3-South", 2002, 6))
            .OnTitle(ShowTitle(2190, "South Park", 1997, 6)).OnSeason(2190, Season(6, "Jared Has Aides"));

        var result = await Identify(Episode("South Park", 1997, 6, 1) with { AirYear = 2002 });

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(2190, result.Title!.TmdbId);
        Assert.Equal([("South Park", (int?)1997)], _tmdb.Searches.Select(s => (s.Title, s.Year)));
    }

    [Theory]
    [InlineData(2009, IdentificationStatus.Matched)] // the 2011 show started after it
    [InlineData(2012, IdentificationStatus.NeedsReview)] // both started before it: still a tie
    public async Task EpisodeAirYear_OnlyAShowThatStartedLaterLosesTheTie(int airYear, IdentificationStatus expected)
    {
        _tmdb.OnSearch("Shameless", null, TitleKind.Show, Show(1906, "Shameless", 2004, 20), Show(34307, "Shameless", 2011, 20))
            .OnTitle(ShowTitle(1906, "Shameless", 2004, 2)).OnSeason(1906, Season(2, "Episode 1"));

        var result = await Identify(Episode("Shameless", null, 2, 1) with { AirYear = airYear });

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task EpisodeAirYear_ShowThatStartedAYearLater_CanStillBeAccepted()
    {
        _tmdb.OnSearch("Some Show", null, TitleKind.Show, Show(77, "Some Show", 2005))
            .OnTitle(ShowTitle(77, "Some Show", 2005, 1)).OnSeason(77, Season(1, "Pilot"));

        var result = await Identify(Episode("Some Show", null, 1, 1) with { AirYear = 2004 });

        Assert.Equal(IdentificationStatus.Matched, result.Status);
    }

    [Fact]
    public async Task CompletePackYear_AfterTheShowStarted_StillMatches()
    {
        _tmdb.OnSearch("Breaking Bad", null, TitleKind.Show, Show(1396, "Breaking Bad", 2008))
            .OnTitle(ShowTitle(1396, "Breaking Bad", 2008, 1)).OnSeason(1396, Season(1, "Pilot"));
        var parsed = PathParser.Parse(@"Breaking Bad (2013) Complete BluRay\Season 1\Breaking.Bad.S01E01.mkv", []);

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(1396, result.Title!.TmdbId);
    }

    [Theory]
    [InlineData(@"Frasier (2023)\Season 2\Frasier.S02E01.mkv")] // the year in the show folder
    [InlineData("Frasier.2023.S02E01.1080p.WEB.h264-ETHEL.mkv")] // the year before the episode marker
    public async Task ShowYear_InTheFolderOrBeforeTheMarker_PicksTheRebootOverTheOriginal(string path)
    {
        _tmdb.OnSearch("Frasier", 2023, TitleKind.Show, Show(194764, "Frasier", 2023, 40, votes: 150))
            .OnSearch("Frasier", null, TitleKind.Show, Show(3452, "Frasier", 1993, 60, votes: 1900), Show(194764, "Frasier", 2023, 40, votes: 150))
            .OnTitle(ShowTitle(3452, "Frasier", 1993, 2)).OnSeason(3452, Season(2, "Episode 1"))
            .OnTitle(ShowTitle(194764, "Frasier", 2023, 2)).OnSeason(194764, Season(2, "Episode 1"));
        var parsed = PathParser.Parse(path, []);

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(194764, result.Title!.TmdbId);
    }

    [Fact]
    public async Task SameTitleNoYear_AirYearIsATiedShowsStartYear_VotesDoNotDecide()
    {
        _tmdb.OnSearch("Frasier", null, TitleKind.Show,
                Show(3452, "Frasier", 1993, 60, votes: 1900), Show(194764, "Frasier", 2023, 40, votes: 150))
            .OnTitle(ShowTitle(3452, "Frasier", 1993, 2)).OnSeason(3452, Season(2, "Episode 1"));

        var result = await Identify(Episode("Frasier", null, 2, 1) with { AirYear = 2023 });

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task SameTitleNoYear_AirYearIsTheLeadersStartYear_VotesStillDecide()
    {
        _tmdb.OnSearch("Battlestar Galactica", null, TitleKind.Show,
                Show(1972, "Battlestar Galactica", 2004, 60, votes: 3000), Show(501, "Battlestar Galactica", 1978, 20, votes: 200))
            .OnTitle(ShowTitle(1972, "Battlestar Galactica", 2004, 1)).OnSeason(1972, Season(1, "33"));

        var result = await Identify(Episode("Battlestar Galactica", null, 1, 1) with { AirYear = 2004 });

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(1972, result.Title!.TmdbId);
    }

    [Fact]
    public async Task SameTitleNoYear_AirYearMatchingNoTiedShow_VotesStillDecide()
    {
        KnowSupernatural(leaderVotes: 8789, runnerUpVotes: 11);

        var result = await Identify(Episode("Supernatural", null, 1, 1) with { AirYear = 2006 });

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(1622, result.Title!.TmdbId);
    }

    [Fact]
    public async Task MovieYearIsStrict()
    {
        _tmdb.OnSearch("Dune", 1984, TitleKind.Movie, Film(2, "Dune", 2021, 5));

        var result = await Identify(Movie("Dune", 1984));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task Specials_AreMatchedByEpisodeTitle()
    {
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 0, 1))
            .OnSeason(91239, Season(1, "Pilot", "Two"))
            .OnSeason(91239, Season(0, "Christmas Special"));
        var parsed = Episode("Brassic", 2019, 1, 1) with { EpisodeTitles = ["Christmas Special"] };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(0, result.Episodes.Single().Season);
        Assert.Equal([(91239, 1), (91239, 0)], _tmdb.SeasonFetches);
    }

    [Fact]
    public async Task EpisodeNumberMissingFromSeason_IsFoundByTitleInAnotherSeason()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 13) with { EpisodeTitles = ["Bend Her"] };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal((5, 13, "Bend Her"), (episode.Season, episode.Number, episode.Name));
        // Each season once; season 0 was read only because the file has an episode title.
        Assert.Equal([(615, 0), (615, 1), (615, 4), (615, 5)], _tmdb.SeasonFetches.Order());
    }

    [Fact]
    public async Task EpisodeNumberMissingFromSeason_TitleNowhere_GoesToReview()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 13) with { EpisodeTitles = ["Not An Episode"] };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task EpisodeTitleAgreeingWithItsNumber_StaysPut_WithoutReadingOtherSeasons()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 2) with { EpisodeTitles = ["Season Four 2"] };

        var result = await Identify(parsed);

        Assert.Equal((4, 2), (result.Episodes.Single().Season, result.Episodes.Single().Number));
        Assert.DoesNotContain((615, 5), _tmdb.SeasonFetches);
    }

    [Fact]
    public async Task EpisodeTitleDisagreeingWithItsNumber_MovesToTheTitledEpisode()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 2) with { EpisodeTitles = ["Bend Her"] };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal((5, 13, "Bend Her"), (episode.Season, episode.Number, episode.Name));
        Assert.Equal([(615, 0), (615, 1), (615, 4), (615, 5)], _tmdb.SeasonFetches.Order());
    }

    [Fact]
    public async Task EpisodeTitleDisagreeingWithItsNumber_NoStrongMatchElsewhere_IsTheShowWithNoEpisode()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 2) with { EpisodeTitles = ["Not An Episode"] };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(615, result.Title!.TmdbId);
        Assert.Empty(result.Episodes);
    }

    [Fact]
    public async Task MultiEpisodeFile_TitleDisagreeingWithEveryNumber_MovesToTheTitledEpisode()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 1, 2) with { EpisodeTitles = ["Obsoletely Fabulous"] };

        var result = await Identify(parsed);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal((5, 14), (episode.Season, episode.Number));
    }

    [Fact]
    public async Task MultiEpisodeFile_TitleAgreeingWithOneNumber_KeepsTheRange()
    {
        KnowFuturama();
        var parsed = Episode("Futurama", null, 4, 1, 2) with { EpisodeTitles = ["Season Four 1"] };

        var result = await Identify(parsed);

        Assert.Equal([(4, 1), (4, 2)], result.Episodes.Select(e => (e.Season, e.Number)));
        Assert.DoesNotContain((615, 5), _tmdb.SeasonFetches);
    }

    [Fact]
    public async Task EpisodeNumberMissingFromSeason_WithoutATitle_FetchesNoOtherSeason()
    {
        KnowFuturama();

        var result = await Identify(Episode("Futurama", null, 4, 13));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal([(615, 4)], _tmdb.SeasonFetches);
    }

    private void KnowFuturama() =>
        _tmdb.OnSearch("Futurama", null, TitleKind.Show, Show(615, "Futurama", 1999))
            .OnTitle(ShowTitle(615, "Futurama", 1999, 0, 1, 4, 5))
            .OnSeason(615, Season(0, "Futurama: The Lost Adventure"))
            .OnSeason(615, Season(1, "Space Pilot 3000", "The Series Has Landed"))
            .OnSeason(615, Season(4, [.. Enumerable.Range(1, 12).Select(n => $"Season Four {n}")]))
            .OnSeason(615, Season(5, [.. Enumerable.Range(1, 12).Select(n => $"Season Five {n}"), "Bend Her", "Obsoletely Fabulous"]));

    [Fact]
    public async Task SeasonMissingFromShow_GoesToReview_WithoutFetchingIt()
    {
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 1));

        var result = await Identify(Episode("Brassic", 2019, 4, 1));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Empty(_tmdb.SeasonFetches);
    }

    [Fact]
    public async Task MovieInShowFolder_SearchesMovies()
    {
        _tmdb.OnSearch("The Movie", 2012, TitleKind.Movie, Film(55, "The Movie", 2012))
            .OnTitle(FilmTitle(55, "The Movie", 2012));

        var result = await Identify(Movie("The Movie", 2012, showFolder: "Some Show"));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(TitleKind.Movie, result.Title!.Kind);
        Assert.All(_tmdb.Searches, s => Assert.Equal(TitleKind.Movie, s.Kind));
        Assert.Empty(_tmdb.SeasonFetches);
    }

    [Fact]
    public async Task UnknownKind_SearchesBothAndKeepsTheBetter()
    {
        var parsed = new ParsedMedia(MediaKind.Unknown, "Heat", 1995, null, [], [], null, null, null);
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnSearch("Heat", 1995, TitleKind.Show, Show(5, "Warm Front", 1990, 1))
            .OnTitle(FilmTitle(949, "Heat", 1995));

        var result = await Identify(parsed);

        Assert.Equal(949, result.Title!.TmdbId);
        Assert.Equal([TitleKind.Movie, TitleKind.Show], _tmdb.Searches.Select(s => s.Kind));
    }

    [Fact]
    public async Task Movie_DurationFarFromRuntime_IsNotAccepted()
    {
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnTitle(FilmTitle(949, "Heat", 1995, runtimeMin: 170));

        var result = await Identify(Movie("Heat", 1995), durationS: 20 * 60);

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal(949, result.Candidates[0].TmdbId);
    }

    [Fact]
    public async Task Movie_DurationMismatch_StaysPenalised_WhenARetryReturnsTheSameFilm()
    {
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnSearch("Heat", null, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnTitle(FilmTitle(949, "Heat", 1995, runtimeMin: 170));

        var result = await Identify(Movie("Heat", 1995), durationS: 20 * 60);

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal(2, _tmdb.Searches.Count);
        Assert.Single(_tmdb.TitleFetches);
    }

    [Fact]
    public async Task Movie_ShortenedTitle_WithMatchingDuration_IsAccepted()
    {
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnTitle(FilmTitle(949, "Heat", 1995, runtimeMin: 170));

        var result = await Identify(Movie("Heat Extended Cut", 1995), durationS: 168 * 60);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(949, result.Title!.TmdbId);
    }

    [Fact]
    public async Task ShortenedTitle_DoesNotAutoAcceptTheWrongSequel()
    {
        _tmdb.OnSearch("Dune", 2024, TitleKind.Movie, Film(438631, "Dune", 2021, 80))
            .OnTitle(FilmTitle(438631, "Dune", 2021));

        var result = await Identify(Movie("Dune Part Two", 2024));

        Assert.Equal(IdentificationStatus.NeedsReview, result.Status);
        Assert.Equal(438631, result.Candidates[0].TmdbId);
    }

    [Theory]
    [InlineData("South Park. The Streaming Wars", 974691)]
    [InlineData("South Park. The Streaming Wars. Part 2", 993729)]
    public async Task PartNumber_PicksTheExactPart_InBothDirections(string title, int expected)
    {
        _tmdb.OnSearch(title, 2022, TitleKind.Movie,
                Film(974691, "South Park the Streaming Wars", 2022, 2.7),
                Film(993729, "South Park the Streaming Wars Part 2", 2022, 2.3))
            .OnTitle(FilmTitle(974691, "South Park the Streaming Wars", 2022))
            .OnTitle(FilmTitle(993729, "South Park the Streaming Wars Part 2", 2022));

        var result = await Identify(Movie(title, 2022));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(expected, result.Title!.TmdbId);
    }

    [Fact]
    public async Task SequelNumber_PicksTheSequel_WithoutAYear()
    {
        _tmdb.OnSearch("Open Season 2", null, TitleKind.Movie,
                Film(7484, "Open Season", 2006, 30), Film(13690, "Open Season 2", 2008, 20), Film(51170, "Open Season 3", 2010, 20))
            .OnTitle(FilmTitle(13690, "Open Season 2", 2008));

        var result = await Identify(Movie("Open Season 2"));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(13690, result.Title!.TmdbId);
    }

    [Fact]
    public async Task TmdbIdTag_UnknownToTmdb_FallsBackToSearch()
    {
        _tmdb.OnSearch("The Matrix", 1999, TitleKind.Movie, Film(603, "The Matrix", 1999))
            .OnTitle(FilmTitle(603, "The Matrix", 1999));
        var parsed = Movie("The Matrix", 1999) with { TmdbId = 999999 };

        var result = await Identify(parsed);

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal(603, result.Title!.TmdbId);
        Assert.Equal("auto", result.Source);
        Assert.Equal([(999999, TitleKind.Movie), (603, TitleKind.Movie)], _tmdb.TitleFetches);
    }

    [Fact]
    public async Task NfoId_UnknownToTmdb_FallsBackToSearch()
    {
        WriteNfo("Heat.nfo", "<movie><tmdbid>111</tmdbid></movie>");
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, Film(949, "Heat", 1995))
            .OnTitle(FilmTitle(949, "Heat", 1995));

        var result = await Identify(Movie("Heat", 1995), Path.Combine(_dir, "Heat.mkv"));

        Assert.Equal(949, result.Title!.TmdbId);
        Assert.Equal("auto", result.Source);
    }

    [Fact]
    public async Task EpisodeWithoutTitle_DoesNotFetchSpecials()
    {
        _tmdb.OnSearch("Brassic", 2019, TitleKind.Show, Show(91239, "Brassic", 2019))
            .OnTitle(ShowTitle(91239, "Brassic", 2019, 0, 1))
            .OnSeason(91239, Season(1, "Pilot", "Two"));

        var result = await Identify(Episode("Brassic", 2019, 1, 2));

        Assert.Equal(IdentificationStatus.Matched, result.Status);
        Assert.Equal([(91239, 1)], _tmdb.SeasonFetches);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline, IdentificationStatus.Offline, null)]
    [InlineData(UnavailableReason.ServerError, IdentificationStatus.Offline, null)]
    [InlineData(UnavailableReason.NoKey, IdentificationStatus.NoKey, null)]
    [InlineData(UnavailableReason.NoKey, IdentificationStatus.NoKey, 949)] // with a TMDB id tag in the name
    [InlineData(UnavailableReason.KeyRejected, IdentificationStatus.KeyRejected, null)]
    public async Task ProviderUnavailable_MapsToStatus(UnavailableReason reason, IdentificationStatus expected, int? tmdbId)
    {
        _tmdb.Unavailable = reason;

        var result = await Identify(Movie("Heat", 1995) with { TmdbId = tmdbId });

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Title);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Identifier(_tmdb).IdentifyAsync(Movie("Heat", 1995), Path.Combine(_dir, "a.mkv"), null, cts.Token));
    }
}
