using System.Net.Http.Headers;

namespace CozyCouch.Core.Metadata.Tmdb;

/// <summary>
/// TMDB issues two kinds of credentials. The v4 read access token is a JWT (starts with "eyJ") and goes
/// in an Authorization header; the v3 API key is a short hex string and goes in the api_key query parameter.
/// </summary>
internal static class TmdbAuth
{
    public static bool IsBearer(string key) => key.StartsWith("eyJ", StringComparison.Ordinal);

    /// <summary>Builds a GET request for <paramref name="path"/> with the query and credentials applied.</summary>
    public static HttpRequestMessage CreateRequest(string baseUrl, string path, string key,
        IEnumerable<KeyValuePair<string, string>> query)
    {
        var pairs = query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}").ToList();
        var bearer = IsBearer(key);
        if (!bearer) pairs.Add($"api_key={Uri.EscapeDataString(key)}");

        var uri = new Uri($"{baseUrl}{path}?{string.Join('&', pairs)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
