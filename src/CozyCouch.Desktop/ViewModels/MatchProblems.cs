using CozyCouch.Core.Activity;
using CozyCouch.Core.Metadata;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>Why a match can't be fixed right now, in the words the title page and the Needs review page both use.</summary>
internal static class MatchProblems
{
    /// <summary>What stands in the way according to the status bar's state; null when TMDB should answer.</summary>
    public static string? Of(StatusSnapshot status) => status switch
    {
        { KeyState: KeyState.Missing } => "Add a TMDB key in Settings",
        { KeyState: KeyState.Rejected } => "TMDB rejected the key. Check it in Settings.",
        { Online: false } => "Connect to the internet to fix this match",
        _ => null,
    };

    /// <summary>What went wrong when TMDB did not answer a fix.</summary>
    public static string Of(UnavailableReason reason) => reason switch
    {
        UnavailableReason.NoKey => "Add a TMDB key in Settings",
        UnavailableReason.KeyRejected => "TMDB rejected the key. Check it in Settings.",
        UnavailableReason.Offline => "Connect to the internet to fix this match",
        _ => "TMDB isn't answering. Try again later.",
    };
}
