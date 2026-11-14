using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Corpus;

/// <summary>
/// Not part of a normal run. With COZYCOUCH_RECORD=1 and COZYCOUCH_TMDB_KEY set, identifies the whole
/// corpus against the live TMDB API, saves every response to Fixtures/tmdb/ in the source tree, and writes
/// Corpus/expected-identification.draft.json for the owner to review.
/// <code>
/// $env:COZYCOUCH_RECORD = "1"; $env:COZYCOUCH_TMDB_KEY = "(the key)"
/// dotnet test --project tests/CozyCouch.Core.Tests -- --filter-trait "Category=Record"
/// </code>
/// </summary>
[Trait("Category", "Record")]
public class RecordFixturesTests
{
    [Fact]
    public async Task RecordCorpus_WritesFixturesAndDraftReference()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(FixtureHandler.RecordVariable) == "1",
            $"Recording is off: set {FixtureHandler.RecordVariable}=1 and {FixtureHandler.KeyVariable} to run it.");
        var key = FixtureHandler.EnvironmentKey;
        Assert.False(string.IsNullOrWhiteSpace(key), $"{FixtureHandler.KeyVariable} is not set.");

        using var handler = FixtureHandler.FromEnvironment();
        Assert.True(handler.IsRecording);

        var items = await CorpusPipeline.IdentifyAsync(handler, key, TimeProvider.System, TestContext.Current.CancellationToken);

        Directory.CreateDirectory(SourceTree.CorpusDirectory);
        ReferenceFile.Write(ReferenceFile.DraftPath, items);
        Assert.NotEmpty(items);
        Assert.NotEmpty(handler.Recorded);
    }
}
