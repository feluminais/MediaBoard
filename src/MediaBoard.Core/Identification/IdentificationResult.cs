using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Identification;

public enum IdentificationStatus { Matched, NeedsReview, Offline, NoKey, KeyRejected }

/// <summary>What the identifier decided for one file.</summary>
/// <param name="Title">The movie or show; set only when <see cref="Status"/> is Matched.</param>
/// <param name="Episodes">The episodes the file holds, ordered by number; empty for movies, unmatched files and a show file whose title names no episode TMDB has.</param>
/// <param name="Confidence">1 for an id match, the winning score for a scored match, the best score for a review, 0 otherwise.</param>
/// <param name="Candidates">The top five candidates by score, best first; empty for a match found by id.</param>
/// <param name="Source">"id_tag" for a name tag or nfo id, "auto" for a scored match or a review, "none" when the provider could not answer.</param>
public sealed record IdentificationResult(
    IdentificationStatus Status,
    TitleDetails? Title,
    IReadOnlyList<EpisodeDetails> Episodes,
    double Confidence,
    IReadOnlyList<SearchCandidate> Candidates,
    string Source);
