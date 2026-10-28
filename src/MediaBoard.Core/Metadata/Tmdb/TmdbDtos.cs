using System.Text.Json.Serialization;

namespace MediaBoard.Core.Metadata.Tmdb;

// Wire shapes for the TMDB v3 endpoints we call. Only the fields we read are declared.

internal sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")] public List<TmdbSearchItem>? Results { get; set; }
}

internal sealed class TmdbSearchItem
{
    [JsonPropertyName("id")] public int Id { get; set; }

    /// <summary>"movie", "tv" or "person"; only present in /search/multi.</summary>
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }

    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("popularity")] public double Popularity { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
}

internal sealed class TmdbFindResponse
{
    [JsonPropertyName("movie_results")] public List<TmdbSearchItem>? MovieResults { get; set; }
    [JsonPropertyName("tv_results")] public List<TmdbSearchItem>? TvResults { get; set; }
}

internal sealed class TmdbMovieDetails
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
}

internal sealed class TmdbTvDetails
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("last_air_date")] public string? LastAirDate { get; set; }

    /// <summary>"Returning Series", "Ended", "Canceled", "In Production", ...</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("episode_run_time")] public List<int>? EpisodeRunTime { get; set; }
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummary>? Seasons { get; set; }
}

internal sealed class TmdbSeasonSummary
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
}

internal sealed class TmdbSeason
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episodes")] public List<TmdbEpisode>? Episodes { get; set; }
}

internal sealed class TmdbEpisode
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
}
