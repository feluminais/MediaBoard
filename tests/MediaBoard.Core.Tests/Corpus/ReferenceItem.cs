using System.Text.Encodings.Web;
using System.Text.Json;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>
/// One top-level item of the owner's library and what identification made of it. The reference list
/// (<c>Corpus/expected-identification.json</c>) is a JSON array of these, sorted by path, camelCase:
/// <code>
/// { "path": "Futurama [WEB-DL1080p]", "kind": "show", "tmdbId": 615, "name": "Futurama", "year": 1999, "status": "matched" }
/// </code>
/// <list type="bullet">
/// <item><c>path</c>: path below the library folder, with backslashes. A top-level folder or a loose
/// top-level file (non-media folders such as a game or an installer never appear). A movie inside a
/// show folder is its own item, with the movie file's relative path. If one top-level folder holds
/// episodes of several different titles, each title is an item with path "folder#tmdbId".</item>
/// <item><c>kind</c>: "movie" or "show". A show folder's kind and id are the show's.</item>
/// <item><c>tmdbId</c>, <c>name</c>, <c>year</c>: the matched TMDB title; <c>tmdbId</c> is null when
/// nothing was matched, and then name/year come from the file name (a provisional title) or are null.</item>
/// <item><c>status</c>: "matched" when every file of the item was matched; otherwise the first of
/// "review", "offline", "pending" that applies. Entries the owner leaves at "review" are items that
/// must stay unmatched; correcting one means setting its tmdbId, name, year and status "matched".</item>
/// </list>
/// The same file shape is written to <c>expected-identification.draft.json</c> by the Record test.
/// </summary>
public sealed record ReferenceItem(string Path, string Kind, int? TmdbId, string? Name, int? Year, string Status)
{
    public override string ToString() =>
        $"{Path} -> {Kind} {(TmdbId is null ? "unmatched" : TmdbId.ToString())} \"{Name}\" ({Year}) [{Status}]";
}

public static class ReferenceFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The owner-reviewed list. Absent until the owner has reviewed the draft.</summary>
    public static string ReviewedPath => System.IO.Path.Combine(SourceTree.CorpusDirectory, "expected-identification.json");

    /// <summary>Written by the Record test; the owner reviews it, then it becomes the reviewed list.</summary>
    public static string DraftPath => System.IO.Path.Combine(SourceTree.CorpusDirectory, "expected-identification.draft.json");

    public static IReadOnlyList<ReferenceItem> Read(string path) =>
        JsonSerializer.Deserialize<List<ReferenceItem>>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{path} is empty.");

    public static void Write(string path, IEnumerable<ReferenceItem> items) =>
        File.WriteAllText(path, JsonSerializer.Serialize(items.OrderBy(i => i.Path, StringComparer.Ordinal).ToList(), Options) + "\n");
}
