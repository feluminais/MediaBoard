using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>The machinery behind the owner-reviewed corpus test: the guard against one episode linked to two files.</summary>
public class CorpusPipelineTests
{
    private static TitleRow Title(int tmdbId, TitleKind kind, string name, int? year) =>
        new(tmdbId + 1000, tmdbId, kind, name, year, null, null, null, null, null);

    private static CorpusPipeline.IdentifiedFile Linked(string path, TitleRow show, string episodeTitle, params (int, int)[] episodes) =>
        new(path, MediaKind.Episode, "matched", show, episodes, [episodeTitle]);

    [Fact]
    public void SharedEpisodes_TwoFilesOnOneEpisode_AreReported()
    {
        var futurama = Title(615, TitleKind.Show, "Futurama", 1999);

        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"Futurama\S04E01 Kif Gets Knocked Up a Notch.mkv", futurama, "Kif Gets Knocked Up a Notch", (5, 5)),
            Linked(@"Futurama\S05E05 The Beast with a Billion Backs. Part 1.mkv", futurama, "The Beast with a Billion Backs. Part 1", (5, 5)),
            Linked(@"Futurama\S04E02 A Tale of Two Santas.mkv", futurama, "A Tale of Two Santas", (4, 2)),
        ]);

        var problem = Assert.Single(problems);
        Assert.Contains("5x5", problem);
        Assert.Contains("Kif Gets Knocked Up a Notch", problem);
        Assert.Contains("The Beast with a Billion Backs. Part 1", problem);

        // Parts of one title on one episode are reported too.
        var parts = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"Futurama\S05E01 Bender's Big Score. Part 1.mkv", futurama, "Bender's Big Score. Part 1", (0, 1)),
            Linked(@"Futurama\S05E02 Bender's Big Score. Part 2.mkv", futurama, "Bender's Big Score. Part 2", (0, 1)),
        ]);

        Assert.Single(parts);
    }

    [Fact]
    public void SharedEpisodes_SameNumberInTwoShows_IsNoProblem()
    {
        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"A\S01E01.mkv", Title(10, TitleKind.Show, "A", 2000), "Pilot", (1, 1)),
            Linked(@"B\S01E01.mkv", Title(20, TitleKind.Show, "B", 2001), "Pilot", (1, 1)),
        ]);

        Assert.Empty(problems);
    }
}
