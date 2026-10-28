using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Parsing;

public sealed class NfoReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-nfo-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, "movie.nfo");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadIds_ReadsKodiUniqueIds()
    {
        var path = Write("""
            <?xml version="1.0" encoding="UTF-8"?>
            <movie>
              <title>The Matrix</title>
              <uniqueid type="imdb" default="false">tt0133093</uniqueid>
              <uniqueid type="tmdb" default="true">603</uniqueid>
            </movie>
            """);

        Assert.Equal((603, "tt0133093"), NfoReader.ReadIds(path));
    }

    [Fact]
    public void ReadIds_ReadsPlainTags()
    {
        var path = Write("<tvshow><tmdbid>1396</tmdbid><imdbid>TT0903747</imdbid></tvshow>");

        Assert.Equal((1396, "tt0903747"), NfoReader.ReadIds(path));
    }

    [Fact]
    public void ReadIds_ReturnsNullsWhenNoIds()
    {
        var path = Write("<movie><title>Nothing</title><uniqueid type=\"tvdb\">81189</uniqueid></movie>");

        Assert.Equal((null, null), NfoReader.ReadIds(path));
    }

    [Fact]
    public void ReadIds_IgnoresNonNumericTmdbAndOddImdb()
    {
        var path = Write("<movie><tmdbid>abc</tmdbid><imdbid>nope</imdbid></movie>");

        Assert.Equal((null, null), NfoReader.ReadIds(path));
    }

    [Fact]
    public void ReadIds_ReturnsNullsOnMalformedXml()
    {
        var path = Write("<movie><uniqueid type=\"tmdb\">603</uniqueid>");

        Assert.Equal((null, null), NfoReader.ReadIds(path));
    }

    [Fact]
    public void ReadIds_ReturnsNullsWhenFileIsMissing()
    {
        Assert.Equal((null, null), NfoReader.ReadIds(Path.Combine(_dir, "missing.nfo")));
    }
}
