using System.Text.Json;

namespace MediaBoard.Core.Remote;

/// <summary>The JSON settings both ends of the API share.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
