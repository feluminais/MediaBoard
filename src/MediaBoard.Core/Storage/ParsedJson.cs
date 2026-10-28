using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Storage;

/// <summary>
/// How <see cref="ParsedMedia"/> is stored in files.parsed_json. The scanner compares the text of two
/// results to tell whether a re-parse changed anything, so the output must stay deterministic.
/// </summary>
internal static class ParsedJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ParsedMedia parsed) => JsonSerializer.Serialize(parsed, Options);

    public static ParsedMedia Deserialize(string json) =>
        JsonSerializer.Deserialize<ParsedMedia>(json, Options)
        ?? throw new InvalidOperationException("files.parsed_json is null.");
}
