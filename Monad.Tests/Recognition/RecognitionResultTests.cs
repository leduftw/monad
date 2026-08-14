using System.Text.Json;

using FluentAssertions;

using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Recognition;

public sealed class RecognitionResultTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // --- FromAuddResult ---

    [Fact]
    public void FromAuddResult_ReadsArtistTitleAndIsrc()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"Serge Gainsbourg","title":"Couleur Café","isrc":"FRZ036400450"}"""));

        // Assert
        result.Artist.Should().Be("Serge Gainsbourg");
        result.Title.Should().Be("Couleur Café");
        result.IsrcInfo.Selected.Should().Be("FRZ036400450");
    }

    [Fact]
    public void FromAuddResult_TrimsSurroundingWhitespace()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"  Eartha Kitt  ","title":"  Je Cherche Un Homme  "}"""));

        // Assert
        result.Artist.Should().Be("Eartha Kitt");
        result.Title.Should().Be("Je Cherche Un Homme");
    }

    [Theory]
    [InlineData("""{"artist":12345}""")]
    [InlineData("""{"artist":null}""")]
    [InlineData("""{"artist":{"name":"x"}}""")]
    [InlineData("""{"artist":["x"]}""")]
    [InlineData("{}")]
    public void FromAuddResult_WithAMissingOrMistypedArtist_UsesAnEmptyString(string json)
    {
        RecognitionResult.FromAuddResult(Parse(json)).Artist.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public void FromAuddResult_WithSomethingOtherThanAnObject_ProducesEmptyFields(string json)
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(json));

        // Assert
        result.Artist.Should().BeEmpty();
        result.Title.Should().BeEmpty();
        result.IsrcInfo.Selected.Should().BeNull();
    }

    [Fact]
    public void FromAuddResult_PreservesNonLatinScripts()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"Бијело Дугме","title":"Ђурђевдан"}"""));

        // Assert
        result.Artist.Should().Be("Бијело Дугме");
        result.Title.Should().Be("Ђурђевдан");
    }

    [Fact]
    public void FromAuddResult_PreservesDiacritics()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"Đorđe Balašević","title":"Računajte Na Nas"}"""));

        // Assert
        result.Artist.Should().Be("Đorđe Balašević");
        result.Title.Should().Be("Računajte Na Nas");
    }

    // --- DisplayText ---

    [Fact]
    public void DisplayText_WithArtistAndTitle_JoinsThemAndAppendsTheIsrc()
    {
        // Arrange
        RecognitionResult result = new("Isabelle Antena", "Le Poisson Des Mers Du Sud", IsrcInfo.None with
        {
            Selected = "GB5EM1001054",
            Suffix = "[ISRC: GB5EM1001054]",
        });

        // Act / Assert
        result.DisplayText.Should().Be("Isabelle Antena - Le Poisson Des Mers Du Sud [ISRC: GB5EM1001054]");
    }

    [Fact]
    public void DisplayText_WithOnlyATitle_OmitsTheSeparator()
    {
        // Arrange — the old formatting trimmed stray dashes off the ends, which
        // quietly mangled titles that legitimately started or ended with one.
        RecognitionResult result = new(string.Empty, "Untitled", IsrcInfo.None);

        // Act / Assert
        result.DisplayText.Should().Be("Untitled [ISRC: n/a]");
    }

    [Fact]
    public void DisplayText_WithOnlyAnArtist_OmitsTheSeparator()
    {
        new RecognitionResult("Nina Simone", string.Empty, IsrcInfo.None)
            .DisplayText.Should().Be("Nina Simone [ISRC: n/a]");
    }

    [Fact]
    public void DisplayText_WithNeither_SaysSo()
    {
        new RecognitionResult(string.Empty, string.Empty, IsrcInfo.None)
            .DisplayText.Should().Be("Unidentified track [ISRC: n/a]");
    }

    [Fact]
    public void DisplayText_KeepsLeadingAndTrailingDashesInTitles()
    {
        // Arrange
        RecognitionResult result = new("Artist", "- Interlude -", IsrcInfo.None);

        // Act / Assert
        result.DisplayText.Should().Be("Artist - - Interlude - [ISRC: n/a]");
    }
}
