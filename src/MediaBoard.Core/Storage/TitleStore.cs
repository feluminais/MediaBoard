using System.Globalization;
using Dapper;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Storage;

/// <param name="TmdbId">Null for a provisional title made from a file name.</param>
/// <param name="FetchedAt">When the details came from TMDB, in UTC; null for a provisional title.</param>
public sealed record TitleRow(
    int Id,
    int? TmdbId,
    TitleKind Kind,
    string Name,
    int? Year,
    int? EndYear,
    string? Overview,
    double? Rating,
    string? PosterPath,
    DateTime? FetchedAt);

/// <summary>A provisional title and one parsed file linked to it.</summary>
public sealed record ProvisionalLink(int TitleId, TitleKind Kind, int FolderId, ParsedMedia Parsed);

/// <summary>Titles (movies and shows) and their episodes.</summary>
public sealed class TitleStore(LibraryDatabase db, TimeProvider? timeProvider = null)
{
    private const string Select =
        "SELECT id, tmdb_id AS TmdbId, kind, name, year, end_year AS EndYear, overview, rating, " +
        "poster_path AS PosterPath, fetched_at AS FetchedAt FROM titles";

    private const string DateFormat = "yyyy-MM-dd";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Inserts the title, or refreshes the stored one with the same TMDB id and kind. Returns its row id.</summary>
    public int Upsert(TitleDetails title)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO titles(tmdb_id, kind, name, year, end_year, overview, rating, poster_path, fetched_at)
            VALUES (@TmdbId, @kind, @Name, @Year, @EndYear, @Overview, @Rating, @PosterPath, @fetchedAt)
            ON CONFLICT(kind, tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET
                name = excluded.name, year = excluded.year, end_year = excluded.end_year,
                overview = excluded.overview, rating = excluded.rating, poster_path = excluded.poster_path,
                fetched_at = excluded.fetched_at
            RETURNING id
            """,
            new
            {
                title.TmdbId,
                kind = KindText(title.Kind),
                title.Name,
                title.Year,
                title.EndYear,
                title.Overview,
                title.Rating,
                title.PosterPath,
                fetchedAt = FileStore.FormatTime(_time.GetUtcNow()),
            });
    }

    /// <summary>
    /// Adds a title that has no TMDB id yet, named after the file. Always a new row: the caller decides which
    /// files share one, because only it knows the folder and show folder they came from.
    /// </summary>
    public int UpsertProvisional(string name, TitleKind kind, int? year)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO titles(tmdb_id, kind, name, year) VALUES (NULL, @kind, @name, @year) RETURNING id",
            new { kind = KindText(kind), name, year });
    }

    /// <summary>Stores every episode of the season, refreshing the ones already there.</summary>
    public void UpsertEpisodes(int titleId, SeasonDetails season)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var e in season.Episodes)
        {
            connection.Execute(
                """
                INSERT INTO episodes(title_id, season, number, name, runtime_min, air_date)
                VALUES (@titleId, @Season, @Number, @Name, @RuntimeMin, @airDate)
                ON CONFLICT(title_id, season, number) DO UPDATE SET
                    name = excluded.name, runtime_min = excluded.runtime_min, air_date = excluded.air_date
                """,
                new
                {
                    titleId,
                    e.Season,
                    e.Number,
                    e.Name,
                    e.RuntimeMin,
                    airDate = e.AirDate?.ToString(DateFormat, CultureInfo.InvariantCulture),
                },
                transaction);
        }

        transaction.Commit();
    }

    public TitleRow? Get(int id)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>($"{Select} WHERE id = @id", new { id })?.ToRow();
    }

    public TitleRow? ByTmdb(int tmdbId, TitleKind kind)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(
            $"{Select} WHERE tmdb_id = @tmdbId AND kind = @kind", new { tmdbId, kind = KindText(kind) })?.ToRow();
    }

    /// <summary>The stored episodes of one season, or null when none were stored.</summary>
    public SeasonDetails? GetSeason(int titleId, int season)
    {
        using var connection = db.Open();
        var rows = connection.Query<EpisodeRow>(
            "SELECT season, number, name, runtime_min AS RuntimeMin, air_date AS AirDate FROM episodes " +
            "WHERE title_id = @titleId AND season = @season ORDER BY number",
            new { titleId, season }).ToList();
        if (rows.Count == 0) return null;

        return new SeasonDetails(season, rows.Select(r => new EpisodeDetails(
            (int)r.Season,
            (int)r.Number,
            r.Name ?? "",
            (int?)r.RuntimeMin,
            r.AirDate is null ? null : DateOnly.ParseExact(r.AirDate, DateFormat, CultureInfo.InvariantCulture))).ToList());
    }

    /// <summary>Row ids of the given (season, number) episodes of a title; ones that are not stored are left out.</summary>
    public IReadOnlyList<int> EpisodeIds(int titleId, IEnumerable<(int Season, int Number)> episodes)
    {
        using var connection = db.Open();
        var ids = new List<int>();
        foreach (var (season, number) in episodes)
        {
            var id = connection.ExecuteScalar<long?>(
                "SELECT id FROM episodes WHERE title_id = @titleId AND season = @season AND number = @number",
                new { titleId, season, number });
            if (id is { } found) ids.Add((int)found);
        }

        return ids;
    }

    /// <summary>Every provisional title with each file linked to it, so a run can share them again.</summary>
    public IReadOnlyList<ProvisionalLink> ProvisionalLinks()
    {
        using var connection = db.Open();
        return connection.Query<LinkRow>(
                """
                SELECT t.id AS TitleId, t.kind AS Kind, f.folder_id AS FolderId, f.parsed_json AS ParsedJson
                FROM titles t
                JOIN file_titles ft ON ft.title_id = t.id
                JOIN files f ON f.id = ft.file_id
                WHERE t.tmdb_id IS NULL
                """)
            .Select(r => new ProvisionalLink((int)r.TitleId, ParseKind(r.Kind), (int)r.FolderId, ParsedJson.Deserialize(r.ParsedJson)))
            .ToList();
    }

    /// <summary>Deletes provisional titles that no file or rule points at any more.</summary>
    public void DeleteOrphanProvisional()
    {
        using var connection = db.Open();
        connection.Execute(
            """
            DELETE FROM titles
            WHERE tmdb_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM file_titles WHERE title_id = titles.id)
              AND NOT EXISTS (SELECT 1 FROM match_rules WHERE title_id = titles.id)
            """);
    }

    internal static string KindText(TitleKind kind) => kind == TitleKind.Show ? "show" : "movie";

    private static TitleKind ParseKind(string text) => text == "show" ? TitleKind.Show : TitleKind.Movie;

    private sealed class Row
    {
        public long Id { get; set; }
        public long? TmdbId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public long? Year { get; set; }
        public long? EndYear { get; set; }
        public string? Overview { get; set; }
        public double? Rating { get; set; }
        public string? PosterPath { get; set; }
        public string? FetchedAt { get; set; }

        public TitleRow ToRow() => new(
            (int)Id, (int?)TmdbId, ParseKind(Kind), Name, (int?)Year, (int?)EndYear, Overview, Rating, PosterPath,
            FetchedAt is null ? null : DateTime.Parse(FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private sealed class EpisodeRow
    {
        public long Season { get; set; }
        public long Number { get; set; }
        public string? Name { get; set; }
        public long? RuntimeMin { get; set; }
        public string? AirDate { get; set; }
    }

    private sealed class LinkRow
    {
        public long TitleId { get; set; }
        public string Kind { get; set; } = "";
        public long FolderId { get; set; }
        public string ParsedJson { get; set; } = "";
    }
}
