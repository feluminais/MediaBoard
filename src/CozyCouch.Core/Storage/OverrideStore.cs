using Dapper;
using CozyCouch.Core.Library;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Storage;

public sealed record OverrideRow(TargetKind Kind, int TargetId, string? DisplayName, bool Hidden);

/// <summary>
/// The overrides layer: display names and hidden flags the user set over the scanned files. A target is a
/// title, season, episode, file or folder; the table has no foreign key, and the ids are never reused.
/// </summary>
public sealed class OverrideStore(LibraryDatabase db)
{
    public IReadOnlyList<OverrideRow> All()
    {
        using var connection = db.Open();
        return connection.Query<Row>(
                "SELECT target_kind AS Kind, target_id AS TargetId, display_name AS DisplayName, hidden AS Hidden FROM overrides")
            .Select(r => new OverrideRow(ParseKind(r.Kind), (int)r.TargetId, r.DisplayName, r.Hidden != 0))
            .ToList();
    }

    /// <summary>Sets the display name; null or blank clears it.</summary>
    public void SetDisplayName(TargetKind kind, int id, string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            INSERT INTO overrides(target_kind, target_id, display_name, hidden) VALUES (@kind, @id, @text, 0)
            ON CONFLICT(target_kind, target_id) DO UPDATE SET display_name = excluded.display_name
            """,
            new { kind = KindText(kind), id, text }, transaction);
        Prune(connection, transaction, kind, id);
        transaction.Commit();
    }

    public void SetHidden(TargetKind kind, int id, bool hidden)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            INSERT INTO overrides(target_kind, target_id, display_name, hidden) VALUES (@kind, @id, NULL, @hidden)
            ON CONFLICT(target_kind, target_id) DO UPDATE SET hidden = excluded.hidden
            """,
            new { kind = KindText(kind), id, hidden = hidden ? 1 : 0 }, transaction);
        Prune(connection, transaction, kind, id);
        transaction.Commit();
    }

    /// <summary>An override with no name and no hidden flag says nothing, so its row goes.</summary>
    private static void Prune(SqliteConnection connection, SqliteTransaction transaction, TargetKind kind, int id) =>
        connection.Execute(
            "DELETE FROM overrides WHERE target_kind = @kind AND target_id = @id AND display_name IS NULL AND hidden = 0",
            new { kind = KindText(kind), id }, transaction);

    private static string KindText(TargetKind kind) => kind.ToString().ToLowerInvariant();

    private static TargetKind ParseKind(string text) => Enum.Parse<TargetKind>(text, ignoreCase: true);

    private sealed class Row
    {
        public string Kind { get; set; } = "";
        public long TargetId { get; set; }
        public string? DisplayName { get; set; }
        public long Hidden { get; set; }
    }
}
