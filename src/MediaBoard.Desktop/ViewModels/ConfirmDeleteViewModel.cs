using System.Globalization;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <param name="Note">Set when Windows may delete the file for good instead of using the Recycle Bin.</param>
public sealed record PathRow(string Path, string? Note);

/// <summary>What the "Delete from disk" confirmation says: every path, the size, and the warnings.</summary>
public sealed class ConfirmDeleteViewModel(DeletePreview preview)
{
    public const string SeedingWarning = "Torrent clients seeding these files will lose them.";

    public string Heading { get; } = $"Delete {Plural.Of(preview.Files.Count, "file")} of {preview.TitleName}?";

    public string Intro => "These files go to the Recycle Bin.";

    public IReadOnlyList<PathRow> Paths { get; } = preview.Files
        .Select(f => new PathRow(f.Path, f.MayDeletePermanently ? "May be deleted permanently" : null))
        .ToList();

    public string SizeText { get; } = FormatSize(preview.TotalSize);

    public string Seeding => SeedingWarning;

    /// <summary>Null unless a file is on a network or removable drive.</summary>
    public string? PermanentWarning { get; } = preview.AnyMayDeletePermanently
        ? "Files on a network or removable drive may be deleted permanently."
        : null;

    public bool HasPermanentWarning => PermanentWarning is not null;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

internal static class Plural
{
    /// <summary>"1 file", "2 files".</summary>
    public static string Of(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
