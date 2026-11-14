using Dapper;
using CozyCouch.Core.Identification;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Storage;

/// <summary>A show folder and parsed title whose episodes belong to one title.</summary>
public sealed record MatchRule(int FolderId, string ShowFolder, string ParsedTitle);

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
    /// user's own match, nor a file the user marked "not a movie or show": the write is skipped and false is returned.
    /// </summary>
    public bool SetMatch(int fileId, int titleId, IReadOnlyList<int> episodeIds, double confidence, string source)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (source != UserSource && UserDecided(connection, transaction, fileId)) return false;

        WriteMatch(connection, transaction, fileId, titleId, episodeIds, confidence, source);
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// The user's own choice for one file, in one transaction: the match, the rule for its show folder and
    /// parsed title (when given), and the other files that rule covers and that wait in "review" or "offline"
    /// (episodes of that folder and show with no user match of their own), set back to "pending" so the next
    /// identification run applies the rule to them. Returns how many files were set back.
    /// </summary>
    public int SetUserMatch(int fileId, int titleId, IReadOnlyList<int> episodeIds, MatchRule? rule)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        WriteMatch(connection, transaction, fileId, titleId, episodeIds, 1.0, UserSource);
        if (rule is null)
        {
            transaction.Commit();
            return 0;
        }

        var key = TitleNormalizer.Normalize(rule.ParsedTitle);
        connection.Execute(
            """
            INSERT INTO match_rules(folder_id, show_folder, parsed_title, title_id)
            VALUES (@FolderId, @ShowFolder, @key, @titleId)
            ON CONFLICT(folder_id, show_folder, parsed_title) DO UPDATE SET title_id = excluded.title_id
            """,
            new { rule.FolderId, rule.ShowFolder, key, titleId }, transaction);

        var requeued = 0;
        var waiting = connection.Query<WaitingRow>(
            """
            SELECT id, parsed_json AS ParsedJson FROM files
            WHERE folder_id = @FolderId AND id <> @fileId AND status IN ('review', 'offline')
              AND NOT EXISTS (SELECT 1 FROM file_titles WHERE file_id = files.id AND source = 'user')
            """,
            new { rule.FolderId, fileId }, transaction).ToList();
        foreach (var row in waiting)
        {
            var parsed = ParsedJson.Deserialize(row.ParsedJson);
            if (parsed.Kind != MediaKind.Episode || parsed.ShowFolder != rule.ShowFolder
                || TitleNormalizer.Normalize(parsed.Title) != key) continue;

            SetStatus(connection, transaction, (int)row.Id, "pending");
            requeued++;
        }

        transaction.Commit();
        return requeued;
    }

    private static void WriteMatch(
        SqliteConnection connection, SqliteTransaction transaction, int fileId, int titleId,
        IReadOnlyList<int> episodeIds, double confidence, string source)
    {
        var placeholder = PlaceholderOf(connection, transaction, fileId);
        ClearLinks(connection, transaction, fileId);
        LinkTitle(connection, transaction, fileId, titleId, confidence, source);
        KeepHidden(connection, transaction, fileId, placeholder, titleId);
        foreach (var episodeId in episodeIds)
        {
            connection.Execute(
                "INSERT INTO file_episodes(file_id, episode_id) VALUES (@fileId, @episodeId) ON CONFLICT DO NOTHING",
                new { fileId, episodeId }, transaction);
        }

        SetStatus(connection, transaction, fileId, "matched");
    }

    private sealed class WaitingRow
    {
        public long Id { get; set; }
        public string ParsedJson { get; set; } = "";
    }

    /// <summary>
    /// Gives a file TMDB could not place a provisional title (confidence 0) and the status "offline", so it
    /// stays playable and listed until the lookup can run again. Skipped (false) for a file with a user match or
    /// one marked "not a movie or show", and for a file queued again while still linked automatically to a TMDB
    /// title (after a parser update): it keeps that title and its episodes, and stays pending for the next run.
    /// </summary>
    public bool SetProvisional(int fileId, int provisionalTitleId)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (UserDecided(connection, transaction, fileId) || HasAutomaticTmdbLink(connection, transaction, fileId)) return false;

        var placeholder = PlaceholderOf(connection, transaction, fileId);
        ClearLinks(connection, transaction, fileId);
        LinkTitle(connection, transaction, fileId, provisionalTitleId, 0, "auto");
        KeepHidden(connection, transaction, fileId, placeholder, provisionalTitleId);
        SetStatus(connection, transaction, fileId, "offline");
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Stores the best five candidates (replacing earlier ones) and marks the file "review". With a
    /// provisional title the file keeps that title until the user decides. The candidates arrive best
    /// first; the stored score is that rank (1.0 for the best, 0.1 less for each place after it).
    /// Skipped (false) for a file with a user match or one marked "not a movie or show".
    /// </summary>
    public bool SetReview(int fileId, IReadOnlyList<SearchCandidate> candidates, int? provisionalTitleId = null)
    {
        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (UserDecided(connection, transaction, fileId)) return false;

        var placeholder = PlaceholderOf(connection, transaction, fileId);
        ClearLinks(connection, transaction, fileId);
        if (provisionalTitleId is { } titleId) LinkTitle(connection, transaction, fileId, titleId, 0, "auto");
        KeepHidden(connection, transaction, fileId, placeholder, provisionalTitleId);

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

    /// <summary>Puts a file the user matched back to "matched" after a rescan reset it. A file marked "not a movie or show" since stays so.</summary>
    public void RestoreMatched(int fileId)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE files SET status = 'matched' WHERE id = @fileId AND status <> 'not_media'", new { fileId });
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

    /// <summary>
    /// A placeholder (provisional) title the user renamed is about to go: its files were identified. When every one
    /// of <paramref name="formerFileIds"/> now belongs to one title, and that title has no name of the user's own,
    /// it takes <paramref name="name"/>. Nothing happens while a file is still on the placeholder.
    /// </summary>
    /// <returns>True when the name was carried over.</returns>
    public bool CarryPlaceholderName(int placeholderId, IReadOnlyCollection<int> formerFileIds, string name)
    {
        if (formerFileIds.Count == 0) return false;

        using var connection = db.Open();
        using var transaction = BeginWrite(connection);
        if (connection.ExecuteScalar<int>(
                "SELECT EXISTS (SELECT 1 FROM file_titles WHERE title_id = @placeholderId)", new { placeholderId }, transaction) != 0)
        {
            return false;
        }

        var titles = connection.Query<long>(
            "SELECT DISTINCT title_id FROM file_titles WHERE file_id IN @formerFileIds", new { formerFileIds }, transaction).ToList();
        var linked = connection.ExecuteScalar<int>(
            "SELECT COUNT(DISTINCT file_id) FROM file_titles WHERE file_id IN @formerFileIds", new { formerFileIds }, transaction);
        if (titles.Count != 1 || linked != formerFileIds.Count) return false;

        var carried = connection.Execute(
            """
            INSERT INTO overrides(target_kind, target_id, display_name, hidden) VALUES ('title', @titleId, @name, 0)
            ON CONFLICT(target_kind, target_id) DO UPDATE SET display_name = excluded.display_name
            WHERE overrides.display_name IS NULL
            """,
            new { titleId = titles[0], name }, transaction);
        transaction.Commit();
        return carried > 0;
    }

    /// <summary>The provisional title the file is on, if any.</summary>
    private static int? PlaceholderOf(SqliteConnection connection, SqliteTransaction transaction, int fileId) =>
        (int?)connection.ExecuteScalar<long?>(
            """
            SELECT ft.title_id FROM file_titles ft JOIN titles t ON t.id = ft.title_id
            WHERE ft.file_id = @fileId AND t.tmdb_id IS NULL LIMIT 1
            """,
            new { fileId }, transaction);

    /// <summary>
    /// A file leaving a placeholder title the user hid stays hidden: the title-level flag becomes a file-level one,
    /// since the placeholder goes once it has no files.
    /// </summary>
    private static void KeepHidden(SqliteConnection connection, SqliteTransaction transaction, int fileId, int? placeholder, int? newTitleId)
    {
        if (placeholder is not { } from || from == newTitleId) return;

        connection.Execute(
            """
            INSERT INTO overrides(target_kind, target_id, display_name, hidden)
            SELECT 'file', @fileId, NULL, 1
            WHERE EXISTS (SELECT 1 FROM overrides WHERE target_kind = 'title' AND target_id = @from AND hidden = 1)
            ON CONFLICT(target_kind, target_id) DO UPDATE SET hidden = 1
            """,
            new { fileId, from }, transaction);
    }

    /// <summary>Takes the write lock up front, so the user-link check and the write cannot be split by another writer.</summary>
    private static SqliteTransaction BeginWrite(SqliteConnection connection) => connection.BeginTransaction(deferred: false);

    private static bool HasUserLink(SqliteConnection connection, SqliteTransaction transaction, int fileId) =>
        connection.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM file_titles WHERE file_id = @fileId AND source = 'user')",
            new { fileId }, transaction) != 0;

    /// <summary>
    /// The user matched the file or marked it "not a movie or show", possibly while an identification pass was
    /// working on an older read of it. Checked inside the write transaction, so the pass can't undo either.
    /// </summary>
    private static bool UserDecided(SqliteConnection connection, SqliteTransaction transaction, int fileId) =>
        HasUserLink(connection, transaction, fileId)
        || connection.ExecuteScalar<int>(
            "SELECT EXISTS (SELECT 1 FROM files WHERE id = @fileId AND status = 'not_media')", new { fileId }, transaction) != 0;

    private static bool HasAutomaticTmdbLink(SqliteConnection connection, SqliteTransaction transaction, int fileId) =>
        connection.ExecuteScalar<int>(
            """
            SELECT EXISTS (SELECT 1 FROM file_titles ft JOIN titles t ON t.id = ft.title_id
                           WHERE ft.file_id = @fileId AND ft.source IN ('auto', 'id_tag') AND t.tmdb_id IS NOT NULL)
            """,
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
