using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>
/// The owner's whole real library, identified offline from the recorded TMDB responses, compared with the
/// list the owner reviewed (<c>Corpus/expected-identification.json</c>, format in <see cref="ReferenceItem"/>).
/// </summary>
public class CorpusIdentificationTests
{
    [Fact]
    public async Task EveryItemMatchesReference()
    {
        Assert.SkipUnless(
            File.Exists(ReferenceFile.ReviewedPath),
            "Reference not reviewed yet: run the Record tests with MEDIABOARD_TMDB_KEY, " +
            "then have the owner review expected-identification.draft.json");
        var expected = ReferenceFile.Read(ReferenceFile.ReviewedPath);

        using var handler = new FixtureHandler(SourceTree.FixtureDirectory);
        var files = await ReplayAsync(handler);

        var problems = Compare(expected, CorpusPipeline.Group(files));
        Assert.True(problems.Count == 0, $"{problems.Count} item(s) differ from the reference:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// No TMDB episode is linked to two files. Runs whenever a recording has been made (its draft or the reviewed
    /// list is there), whether or not the owner has reviewed it yet.
    /// </summary>
    [Fact]
    public async Task NoEpisodeIsLinkedToTwoFiles()
    {
        Assert.SkipUnless(
            File.Exists(ReferenceFile.DraftPath) || File.Exists(ReferenceFile.ReviewedPath),
            "No recording yet: run the Record tests with MEDIABOARD_TMDB_KEY");

        using var handler = new FixtureHandler(SourceTree.FixtureDirectory);
        var files = await ReplayAsync(handler);

        var shared = CorpusPipeline.SharedEpisodes(files);
        Assert.True(shared.Count == 0, $"{shared.Count} episode(s) linked to several files:\n" + string.Join("\n", shared));
    }

    /// <summary>Replay only: the handler never reaches the network, and a fixture that is missing fails by name.</summary>
    private static async Task<IReadOnlyList<CorpusPipeline.IdentifiedFile>> ReplayAsync(FixtureHandler handler)
    {
        var files = await CorpusPipeline.IdentifyFilesAsync(handler, "replay-key", new FakeClock(), TestContext.Current.CancellationToken);

        Assert.True(handler.MissingFixtures.Count == 0,
            "Missing fixtures (record them with MEDIABOARD_RECORD=1):\n" + string.Join("\n", handler.MissingFixtures.Distinct().Order()));
        return files;
    }

    internal static List<string> Compare(IReadOnlyList<ReferenceItem> expected, IReadOnlyList<ReferenceItem> actual)
    {
        var problems = new List<string>();
        var actualByPath = actual.ToDictionary(i => i.Path, StringComparer.Ordinal);
        var expectedPaths = expected.Select(i => i.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var item in expected)
        {
            if (!actualByPath.TryGetValue(item.Path, out var found)) problems.Add($"missing: {item}");
            else if (found != item) problems.Add($"differs: expected {item}\n         actual   {found}");
        }

        problems.AddRange(actual.Where(i => !expectedPaths.Contains(i.Path)).Select(i => $"unexpected: {i}"));
        return problems;
    }
}
