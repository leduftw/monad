using FluentAssertions;
using Monad.Recognition;
using System.Text.Json;
using Xunit;

namespace Monad.Tests.Recognition;

public sealed class RecognitionResultTests
{
    [Fact]
    public void FromAuddResult_WithValidArtistAndTitle_ReturnsCorrectValues()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""The Beatles"",
            ""title"": ""Hey Jude"",
            ""isrc"": ""GBUM70000001""
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("The Beatles");
        recognitionResult.Title.Should().Be("Hey Jude");
        recognitionResult.IsrcInfo.Selected.Should().Be("GBUM70000001");
    }

    [Fact]
    public void FromAuddResult_WithArtistWhitespace_TrimsWhitespace()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""  Queen  "",
            ""title"": ""Bohemian Rhapsody"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Queen");
        recognitionResult.Title.Should().Be("Bohemian Rhapsody");
    }

    [Fact]
    public void FromAuddResult_WithTitleWhitespace_TrimsWhitespace()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Pink Floyd"",
            ""title"": ""  Comfortably Numb  "",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Pink Floyd");
        recognitionResult.Title.Should().Be("Comfortably Numb");
    }

    [Fact]
    public void FromAuddResult_WithBothWhitespace_TrimsBoth()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""  David Bowie  "",
            ""title"": ""  Space Oddity  "",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("David Bowie");
        recognitionResult.Title.Should().Be("Space Oddity");
    }

    [Fact]
    public void FromAuddResult_WithMissingArtist_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""title"": ""Imagine"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("Imagine");
    }

    [Fact]
    public void FromAuddResult_WithMissingTitle_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""John Lennon"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("John Lennon");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithNullArtist_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": null,
            ""title"": ""Stairway to Heaven"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("Stairway to Heaven");
    }

    [Fact]
    public void FromAuddResult_WithNullTitle_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Led Zeppelin"",
            ""title"": null,
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Led Zeppelin");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithWhitespaceOnlyArtist_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""   "",
            ""title"": ""Song Title"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("Song Title");
    }

    [Fact]
    public void FromAuddResult_WithWhitespaceOnlyTitle_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Artist Name"",
            ""title"": ""   "",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Artist Name");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithBothMissing_ReturnsBothEmpty()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithBothNullAndMissing_ReturnsBothEmpty()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": null,
            ""title"": null,
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithArtistAsNumber_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": 123,
            ""title"": ""Song"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("Song");
    }

    [Fact]
    public void FromAuddResult_WithTitleAsObject_ReturnsEmptyString()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Artist"",
            ""title"": { ""nested"": ""value"" },
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Artist");
        recognitionResult.Title.Should().Be("");
    }

    [Fact]
    public void FromAuddResult_WithCompleteData_ExtractsIsrc()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Radiohead"",
            ""title"": ""Creep"",
            ""isrc"": ""GBUM70000003"",
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000003"" } },
            ""apple_music"": { ""isrc"": ""GBUM70000003"" }
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Radiohead");
        recognitionResult.Title.Should().Be("Creep");
        recognitionResult.IsrcInfo.Selected.Should().Be("GBUM70000003");
        recognitionResult.IsrcInfo.Spotify.Should().Be("GBUM70000003");
        recognitionResult.IsrcInfo.Apple.Should().Be("GBUM70000003");
    }

    [Fact]
    public void FromAuddResult_WithTabsAndNewlines_TrimsWhitespace()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""\t\n  The Who  \n\t"",
            ""title"": ""\t  My Generation  \t"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("The Who");
        recognitionResult.Title.Should().Be("My Generation");
    }

    [Fact]
    public void FromAuddResult_WithEmptyJson_ReturnsAllEmpty()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{}").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("");
        recognitionResult.Title.Should().Be("");
        recognitionResult.IsrcInfo.Selected.Should().BeNull();
    }

    [Fact]
    public void FromAuddResult_WithSpecialCharacters_PreservesCharacters()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""AC/DC"",
            ""title"": ""Back in Black & Blue"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("AC/DC");
        recognitionResult.Title.Should().Be("Back in Black & Blue");
    }

    [Fact]
    public void FromAuddResult_WithUnicodeCharacters_PreservesUnicode()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Édith Piaf"",
            ""title"": ""La Vie en Rose"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("Édith Piaf");
        recognitionResult.Title.Should().Be("La Vie en Rose");
    }

    [Fact]
    public void FromAuddResult_WithCyrillicCharacters_PreservesCyrillic()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""?????? ????"",
            ""title"": ""????? (????? ?????)"",
            ""isrc"": null
        }").RootElement;

        // Act
        RecognitionResult recognitionResult = RecognitionResult.FromAuddResult(result);

        // Assert
        recognitionResult.Artist.Should().Be("?????? ????");
        recognitionResult.Title.Should().Be("????? (????? ?????)");
    }
}
