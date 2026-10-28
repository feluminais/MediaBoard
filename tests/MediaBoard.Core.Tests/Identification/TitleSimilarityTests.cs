using MediaBoard.Core.Identification;

namespace MediaBoard.Core.Tests.Identification;

public class TitleSimilarityTests
{
    [Fact]
    public void Normalize_AmpersandAndPunctuation_MatchEquivalentTitle()
    {
        Assert.Equal(TitleNormalizer.Normalize("Planes Fire and Rescue"), TitleNormalizer.Normalize("Planes: Fire & Rescue"));
    }

    [Theory]
    [InlineData("The Gentlemen", "gentlemen")]
    [InlineData("  Spider-Man:   Across the Spider-Verse ", "spider man across the spider verse")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Ocean’s Eleven", "oceans eleven")]
    [InlineData("Ocean's Eleven", "oceans eleven")]
    [InlineData("Брат 2", "брат 2")]
    [InlineData("Ёжик и Жизнь", "ёжик и жизнь")]
    [InlineData("Йога", "йога")]
    [InlineData("", "")]
    public void Normalize_Cases(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Score_MissingColon_IsNearPerfect()
    {
        Assert.True(TitleSimilarity.Score("Narcos Mexico", "Narcos: Mexico") >= 0.95);
    }

    [Fact]
    public void Score_HyphensAndColons_AreEquivalent()
    {
        Assert.Equal(1.0, TitleSimilarity.Score("Spider-Man: Across the Spider-Verse", "Spider-Man Across the Spider-Verse"));
    }

    [Fact]
    public void Score_Sequel_ScoresBelowExactTitle()
    {
        const string title = "Cloudy With A Chance Of Meatballs";
        Assert.True(
            TitleSimilarity.Score(title, "Cloudy with a Chance of Meatballs 2")
            < TitleSimilarity.Score(title, "Cloudy with a Chance of Meatballs"));
    }

    [Fact]
    public void Score_DiacriticsAndCase_AreIgnored()
    {
        Assert.Equal(1.0, TitleSimilarity.Score("AMELIE", "Amélie"));
    }

    [Fact]
    public void Score_WordOrder_StillScoresWell()
    {
        Assert.True(TitleSimilarity.Score("Rescue Fire", "Fire Rescue") >= 0.55);
    }

    [Fact]
    public void Score_Unrelated_IsLow()
    {
        Assert.True(TitleSimilarity.Score("The Gentlemen", "Breaking Bad") < 0.4);
    }

    [Theory]
    [InlineData("", "Anything")]
    [InlineData("Anything", "")]
    [InlineData("", "")]
    [InlineData("!!!", "Anything")]
    public void Score_EmptyAfterNormalizing_IsZero(string a, string b)
    {
        Assert.Equal(0.0, TitleSimilarity.Score(a, b));
    }

    [Fact]
    public void Score_SymmetricAndInUnitRange()
    {
        var ab = TitleSimilarity.Score("Planes Fire Rescue", "Planes");
        var ba = TitleSimilarity.Score("Planes", "Planes Fire Rescue");
        Assert.Equal(ab, ba);
        Assert.InRange(ab, 0.0, 1.0);
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("same", "same", 0)]
    public void Levenshtein_KnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, TitleSimilarity.Levenshtein(a, b));
    }
}
