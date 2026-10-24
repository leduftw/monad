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
            """{"artist":"Artist A","title":"Song A","isrc":"USS1Z2600001"}"""));

        // Assert
        result.Artist.Should().Be("Artist A");
        result.Title.Should().Be("Song A");
        result.IsrcInfo.Selected.Should().Be("USS1Z2600001");
    }

    [Fact]
    public void FromAuddResult_TrimsSurroundingWhitespace()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"  Artist A  ","title":"  Song A  "}"""));

        // Assert
        result.Artist.Should().Be("Artist A");
        result.Title.Should().Be("Song A");
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
            """{"artist":"Исполнитель","title":"Песня"}"""));

        // Assert
        result.Artist.Should().Be("Исполнитель");
        result.Title.Should().Be("Песня");
    }

    [Fact]
    public void FromAuddResult_PreservesDiacritics()
    {
        // Arrange / Act
        RecognitionResult result = RecognitionResult.FromAuddResult(Parse(
            """{"artist":"Intérprete","title":"Canción"}"""));

        // Assert
        result.Artist.Should().Be("Intérprete");
        result.Title.Should().Be("Canción");
    }

    // --- DisplayText ---

    [Fact]
    public void DisplayText_WithArtistAndTitle_JoinsThemAndAppendsTheIsrc()
    {
        // Arrange
        RecognitionResult result = new("Artist A", "Song A", IsrcInfo.None with
        {
            Selected = "USS1Z2600001",
            Suffix = "[ISRC: USS1Z2600001]",
        });

        // Act / Assert
        result.DisplayText.Should().Be("Artist A - Song A [ISRC: USS1Z2600001]");
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
        new RecognitionResult("Artist A", string.Empty, IsrcInfo.None)
            .DisplayText.Should().Be("Artist A [ISRC: n/a]");
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
