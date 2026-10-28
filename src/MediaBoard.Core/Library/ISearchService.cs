using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Library;

public enum OnlineState { Ok, Offline, NoKey, KeyRejected, TooShort }

/// <param name="LibraryTitleId">The local title with this TMDB id and kind, hidden or not; null when the library has none.</param>
public sealed record OnlineResult(SearchCandidate Candidate, int? LibraryTitleId);

/// <param name="Library">Titles in the library, found without the network.</param>
/// <param name="Online">TMDB hits; empty unless <paramref name="State"/> is <see cref="OnlineState.Ok"/>.</param>
/// <param name="State">Why <paramref name="Online"/> is what it is, for the palette to explain.</param>
public sealed record SearchResults(IReadOnlyList<TitleCard> Library, IReadOnlyList<OnlineResult> Online, OnlineState State);

/// <summary>A <c>&gt;</c> command in the palette.</summary>
public sealed record PaletteCommand(string Id, string Label);

/// <summary>Behind the Ctrl+F palette. It does no debouncing: the palette view model waits 300 ms between keystrokes.</summary>
public interface ISearchService
{
    /// <summary>
    /// Library results are computed before the network is touched and are always returned. A TMDB failure
    /// shows up in <see cref="SearchResults.State"/>; nothing is thrown for it. Cancelling throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<SearchResults> SearchAsync(string query, CancellationToken ct);

    /// <summary>The TMDB details of an online hit, or null when TMDB cannot answer.</summary>
    Task<TitleDetails?> GetOnlineDetailsAsync(int tmdbId, TitleKind kind, CancellationToken ct);

    /// <summary>The commands whose label has a word starting with <paramref name="filter"/> (case-insensitive); all of them for an empty filter.</summary>
    IReadOnlyList<PaletteCommand> Commands(string filter);
}
