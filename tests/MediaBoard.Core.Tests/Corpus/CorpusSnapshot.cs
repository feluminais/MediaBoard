using System.Globalization;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>
/// The owner's real library folder as a list of relative paths and sizes (no file content).
/// Regenerate with the PowerShell command in the Task 4 brief.
/// </summary>
public static class CorpusSnapshot
{
    private const string ResourceName = "torrent-downloads.paths.txt";

    public static IReadOnlyList<(string RelativePath, long Size)> Load()
    {
        using var stream = typeof(CorpusSnapshot).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);

        var entries = new List<(string, long)>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var tab = line.LastIndexOf('\t');
            entries.Add((line[..tab], long.Parse(line[(tab + 1)..], CultureInfo.InvariantCulture)));
        }

        return entries;
    }
}
