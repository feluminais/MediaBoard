using System.Runtime.CompilerServices;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// Where the test project lives in the source tree, not in bin/. Recording writes fixtures and the draft
/// reference list there, so they can be reviewed and committed. The path is baked in at compile time by
/// <see cref="CallerFilePathAttribute"/>.
/// </summary>
public static class SourceTree
{
    public static string ProjectDirectory { get; } = Locate();

    /// <summary>Source-tree copy of the recorded TMDB responses.</summary>
    public static string FixtureDirectory => Path.Combine(ProjectDirectory, "Fixtures", "tmdb");

    public static string CorpusDirectory => Path.Combine(ProjectDirectory, "Corpus");

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        // This file is <project>/Support/SourceTree.cs.
        var directory = Path.GetDirectoryName(Path.GetDirectoryName(thisFile));
        if (directory is null || !File.Exists(Path.Combine(directory, "MediaBoard.Core.Tests.csproj")))
            throw new InvalidOperationException(
                "The test project's source directory is not available here (was the test assembly built on another machine?).");
        return directory;
    }
}
