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
        var gap = TitleSimilarity.Score(title, "Cloudy with a Chance of Meatballs")
                  - TitleSimilarity.Score(title, "Cloudy with a Chance of Meatballs 2");
        Assert.True(gap >= 0.03, $"gap was {gap}");
    }

    [Theory]
    [InlineData("Open Season 2", "Open Season 3", "Open Season b", "Open Season c")]
    [InlineData("Rocky", "Rocky 2", "Rocky", "Rocky b")]
    [InlineData("Rocky", "Rocky II", "Rocky", "Rocky bb")]
    public void Score_SequelNumberOnOneSideOrDifferent_CostsThePenalty(string a, string b, string plainA, string plainB)
    {
        // The plain pair differs by letters in the same places, so only the penalty sets the two scores apart.
        Assert.Equal(TitleSimilarity.Score(plainA, plainB) - ScoringWeights.NumberMismatchPenalty, TitleSimilarity.Score(a, b), 9);
    }

    [Theory]
    [InlineData("South Park the Streaming Wars", "South Park the Streaming Wars Part 2", false)]
    [InlineData("Open Season 2", "Open Season 3", false)]
    [InlineData("Dune Part Two", "Dune", false)]
    [InlineData("Rocky 2", "Rocky II", true)]
    [InlineData("Dune Part Two", "Dune: Part 2", true)]
    [InlineData("Malcolm X", "Malcolm", true)]
    [InlineData("Fantastic 4 Rise of the Silver Surfer", "Fantastic Four: Rise of the Silver Surfer", true)]
    [InlineData("Oceans 11", "Ocean's Eleven", true)]
    [InlineData("Twelve Monkeys", "12 Monkeys", true)]
    [InlineData("Cartoon Wars Part I", "Cartoon Wars (1)", true)]
    [InlineData("Kill Bill Vol. 1", "Kill Bill: Volume I", true)]
    [InlineData("Star Wars Episode V", "Star Wars Episode 5", true)]
    [InlineData("Imaginationland Chapter X", "Imaginationland Chapter 9", false)]
    [InlineData("V for Vendetta", "Vendetta", true)]
    public void SameNumbers_ComparesDigitsRomanNumeralsAndPartWords(string a, string b, bool expected)
    {
        Assert.Equal(expected, TitleSimilarity.SameNumbers(TitleNormalizer.Normalize(a), TitleNormalizer.Normalize(b)));
    }

    [Fact]
    public void Score_WordOrder_DoesNotMatter()
    {
        Assert.True(TitleSimilarity.Score("fire rescue", "rescue fire") >= 0.99);
    }

    [Fact]
    public void Score_Unrelated_IsLow()
    {
        Assert.True(TitleSimilarity.Score("The Gentlemen", "Breaking Bad") < 0.4);
    }

    [Theory]
    [InlineData("", "Anything")]
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
    public void Levenshtein_KnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, TitleSimilarity.Levenshtein(a, b));
    }
}
