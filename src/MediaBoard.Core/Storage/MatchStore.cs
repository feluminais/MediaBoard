using Dapper;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Storage;

/// <summary>
/// Which title and episodes each file is, the review candidates for files nobody could place, and the
/// match rules. Every method that changes a file runs in one transaction.
/// </summary>
public sealed class MatchStore(LibraryDatabase db)
{
    private const int ReviewCandidateCount = 5;
    private const double RankScoreStep = 0.1;
    private const string UserSource = "user";

    /// <summary>
    /// Points the file at the title and episodes and marks it matched. Earlier links, provisional ones
    /// included, and its review candidates are replaced. A source other than "user" never replaces the
    /// user's own match: the write is skipped and false is returned.
    /// </summary>
    public bool SetMatch(int fileId, int titleId, IReadOnlyList<int> episodeIds, double confidence, string source)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (source != UserSource && HasUserLink(connection, transaction, fileId)) return false;

        ClearLinks(connection, transaction, fileId);
        LinkTitle(connection, transaction, fileId, titleId, confidence, source);
        foreach (var episodeId in episodeIds)
        {
            connection.Execute(
                "INSERT INTO file_episodes(file_id, episode_id) VALUES (@fileId, @episodeId) ON CONFLICT DO NOTHING",
                new { fileId, episodeId }, transaction);
        }

        SetStatus(connection, transaction, fileId, "matched");
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Gives a file TMDB could not place a provisional title (confidence 0) and the status "offline", so it
    /// stays playable and listed until the lookup can run again. Skipped (false) for a file with a user match.
    /// </summary>
    public bool SetProvisional(int fileId, int provisionalTitleId)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (HasUserLink(connection, transaction, fileId)) return false;

        ClearLinks(connection, transaction, fileId);
        LinkTitle(connection, transaction, fileId, provisionalTitleId, 0, "auto");
        SetStatus(connection, transaction, fileId, "offline");
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Stores the best five candidates (replacing earlier ones) and marks the file "review". With a
    /// provisional title the file keeps that title until the user decides. The candidates arrive best
    /// first; the stored score is that rank (1.0 for the best, 0.1 less for each place after it).
    /// Skipped (false) for a file with a user match.
    /// </summary>
    public bool SetReview(int fileId, IReadOnlyList<SearchCandidate> candidates, int? provisionalTitleId = null)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (HasUserLink(connection, transaction, fileId)) return false;

        ClearLinks(connection, transaction, fileId);
        if (provisionalTitleId is { } titleId) LinkTitle(connection, transaction, fileId, titleId, 0, "auto");

        var rank = 0;
        foreach (var c in candidates.Take(ReviewCandidateCount))
        {
            connection.Execute(
                """
                INSERT INTO review_candidates(file_id, tmdb_id, kind, name, year, score)
                VALUES (@fileId, @TmdbId, @kind, @Name, @Year, @score)
                ON CONFLICT(file_id, kind, tmdb_id) DO UPDATE SET name = excluded.name, year = excluded.year, score = excluded.score
                """,
                new { fileId, c.TmdbId, kind = TitleStore.KindText(c.Kind), c.Name, c.Year, score = 1.0 - RankScoreStep * rank++ },
                transaction);
        }

        SetStatus(connection, transaction, fileId, "review");
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// The user says the file is not a movie or show: its title, episode and candidate links go and the status
    /// becomes "not_media", which identification never touches. A user match is replaced too.
    /// </summary>
    public void SetNotMedia(int fileId)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        ClearLinks(connection, transaction, fileId);
        SetStatus(connection, transaction, fileId, "not_media");
        transaction.Commit();
    }

    public bool HasUserMatch(int fileId)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM file_titles WHERE file_id = @fileId AND source = 'user')", new { fileId }) != 0;
    }

    /// <summary>Puts a file the user matched back to "matched" after a rescan reset it.</summary>
    public void RestoreMatched(int fileId)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE files SET status = 'matched' WHERE id = @fileId", new { fileId });
    }

    /// <summary>
    /// Remembers that files with this parsed title inside this show folder belong to the title. Titles are
    /// compared normalized, so "The.Show" and "the show" are one rule.
    /// </summary>
    public void AddRule(int folderId, string showFolder, string parsedTitle, int titleId)
    {
        using var connection = db.Open();
        connection.Execute(
            """
            INSERT INTO match_rules(folder_id, show_folder, parsed_title, title_id)
            VALUES (@folderId, @showFolder, @key, @titleId)
            ON CONFLICT(folder_id, show_folder, parsed_title) DO UPDATE SET title_id = excluded.title_id
            """,
            new { folderId, showFolder, key = TitleNormalizer.Normalize(parsedTitle), titleId });
    }

    public int? FindRule(int folderId, string showFolder, string parsedTitle)
    {
        using var connection = db.Open();
        var id = connection.ExecuteScalar<long?>(
            "SELECT title_id FROM match_rules WHERE folder_id = @folderId AND show_folder = @showFolder AND parsed_title = @key",
            new { folderId, showFolder, key = TitleNormalizer.Normalize(parsedTitle) });
        return id is { } found ? (int)found : null;
    }

    /// <summary>Takes the write lock up front, so the user-link check and the write cannot be split by another writer.</summary>
    private static SqliteTransaction BeginWrite(SqliteConnection connection) => connection.BeginTransaction(deferred: false);

    private static bool HasUserLink(SqliteConnection connection, SqliteTransaction transaction, int fileId) =>
        connection.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM file_titles WHERE file_id = @fileId AND source = 'user')",
            new { fileId }, transaction) != 0;

    private static void ClearLinks(SqliteConnection connection, SqliteTransaction transaction, int fileId)
    {
        connection.Execute("DELETE FROM file_titles WHERE file_id = @fileId", new { fileId }, transaction);
        connection.Execute("DELETE FROM file_episodes WHERE file_id = @fileId", new { fileId }, transaction);
        connection.Execute("DELETE FROM review_candidates WHERE file_id = @fileId", new { fileId }, transaction);
    }

    private static void LinkTitle(
        SqliteConnection connection, SqliteTransaction transaction, int fileId, int titleId, double confidence, string source) =>
        connection.Execute(
            "INSERT INTO file_titles(file_id, title_id, confidence, source) VALUES (@fileId, @titleId, @confidence, @source)",
            new { fileId, titleId, confidence, source }, transaction);

    private static void SetStatus(SqliteConnection connection, SqliteTransaction transaction, int fileId, string status) =>
        connection.Execute("UPDATE files SET status = @status WHERE id = @fileId", new { fileId, status }, transaction);
}
