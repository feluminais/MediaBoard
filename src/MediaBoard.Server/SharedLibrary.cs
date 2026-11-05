using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;

namespace MediaBoard.Server;

/// <summary>What the library endpoints read from.</summary>
/// <param name="Owner">The owner's library; each request goes through <see cref="LocalLibrary.ForViewer"/>.</param>
/// <param name="SearchFor">
/// A search service for this viewer. It must be built on the viewer's own library and take the same viewer, or a
/// search shows titles the viewer can't reach. The search index and season cache live in the service, so keep it
/// between requests.
/// </param>
public sealed record SharedLibrary(
    LocalLibrary Owner, ViewerAccess Access, FileStore Files, ProgressStore Progress, Func<Viewer, ISearchService> SearchFor, ImageCache Images);
