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

        // Replay only: the handler never reaches the network, and a fixture that is missing is reported by name.
        using var handler = new FixtureHandler(SourceTree.FixtureDirectory);
        var actual = await CorpusPipeline.IdentifyAsync(handler, "replay-key", new FakeClock(), TestContext.Current.CancellationToken);

        Assert.True(handler.MissingFixtures.Count == 0,
            "Missing fixtures (record them with MEDIABOARD_RECORD=1):\n" + string.Join("\n", handler.MissingFixtures.Distinct().Order()));

        var problems = Compare(expected, actual);
        Assert.True(problems.Count == 0, $"{problems.Count} item(s) differ from the reference:\n" + string.Join("\n", problems));
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
