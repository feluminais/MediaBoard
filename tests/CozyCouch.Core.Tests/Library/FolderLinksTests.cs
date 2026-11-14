using CozyCouch.Core.Library;
using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Library;

public sealed class FolderLinksTests : IDisposable
{
    private readonly TempLibrary _root = new();
    private readonly TempLibrary _outside = new();

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void PlainPath_IsNotThroughALink()
    {
        _root.File(@"Show\Season 1\E01.mkv", 16);

        Assert.False(new FolderLinks().IsThroughLink(_root.Root, @"Show\Season 1\E01.mkv"));
    }

    [Fact]
    public void JunctionPartWayDown_IsFound_AtAnyDepth()
    {
        _outside.File(@"Deep\Season 1\E01.mkv", 16);
        _root.Junction(@"Show\Linked", _outside.PathOf("Deep"));
        _root.File(@"Show\Real\E02.mkv", 16);

        var links = new FolderLinks();

        Assert.True(links.IsThroughLink(_root.Root, @"Show\Linked\Season 1\E01.mkv"));
        Assert.False(links.IsThroughLink(_root.Root, @"Show\Real\E02.mkv"));
    }

    [Fact]
    public void FolderThatIsItselfALink_IsWhatTheOwnerChose()
    {
        _outside.File(@"Films\Heat.mkv", 16);
        _root.Junction("Chosen", _outside.PathOf("Films"));

        Assert.False(new FolderLinks().IsThroughLink(_root.PathOf("Chosen"), "Heat.mkv"));
    }

    [Fact]
    public void SymbolicLinkToAFile_IsFound()
    {
        var target = _outside.File("Secret.mkv", 16);
        try
        {
            File.CreateSymbolicLink(_root.PathOf("Innocent.mkv"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make symbolic links.");
        }

        Assert.True(new FolderLinks().IsThroughLink(_root.Root, "Innocent.mkv"));
    }

    [Fact]
    public void PathThatIsNotThere_IsNotThroughALink()
    {
        Assert.False(new FolderLinks().IsThroughLink(_root.PathOf("Gone"), @"Show\E01.mkv"));
    }
}
