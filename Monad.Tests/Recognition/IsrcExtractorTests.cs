using FluentAssertions;
using Monad.Recognition;
using System.Text.Json;
using Xunit;

namespace Monad.Tests.Recognition;

public sealed class IsrcExtractorTests
{
    [Fact]
    public void Extract_WithTopLevelIsrc_SelectsTopLevel()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""USRC17607839"",
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } },
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("USRC17607839");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().Be("GBUM70000002");
        info.Suffix.Should().Be("[ISRC: USRC17607839]");
    }

    [Fact]
    public void Extract_WithSpotifyIsrcOnly_SelectsSpotify()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().BeNull();
        info.Suffix.Should().Be("[ISRC: GBUM70000001]");
    }

    [Fact]
    public void Extract_WithAppleIsrcOnly_SelectsApple()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000002");
        info.Spotify.Should().BeNull();
        info.Apple.Should().Be("GBUM70000002");
        info.Suffix.Should().Be("[ISRC: GBUM70000002]");
    }

    [Fact]
    public void Extract_WithNoIsrc_ReturnsNullWithNaSuffix()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""artist"": ""Unknown Artist"",
            ""title"": ""Unknown Title""
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().BeNull();
        info.Spotify.Should().BeNull();
        info.Apple.Should().BeNull();
        info.Suffix.Should().Be("[ISRC: n/a]");
    }

    [Fact]
    public void Extract_WithIsrcMismatch_ReturnsMismatchMessage()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } },
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().Be("GBUM70000002");
        info.Suffix.Should().Be("[ISRC mismatch: spotify=GBUM70000001, apple=GBUM70000002]");
    }

    [Fact]
    public void Extract_WithIsrcMatch_ReturnsSingleMessage()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } },
            ""apple_music"": { ""isrc"": ""GBUM70000001"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().Be("GBUM70000001");
        info.Suffix.Should().Be("[ISRC: GBUM70000001]");
    }

    [Fact]
    public void Extract_WithWhitespaceOnlyIsrc_TreatsAsNull()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""   "",
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Suffix.Should().Be("[ISRC: GBUM70000001]");
    }

    [Fact]
    public void Extract_WithMissingSpotifyObject_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""USRC17607839""
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("USRC17607839");
        info.Spotify.Should().BeNull();
        info.Apple.Should().BeNull();
    }

    [Fact]
    public void Extract_WithSpotifyNotAnObject_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": ""not an object"",
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000002");
        info.Spotify.Should().BeNull();
        info.Apple.Should().Be("GBUM70000002");
    }

    [Fact]
    public void Extract_WithMissingExternalIds_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""other_field"": ""value"" },
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000002");
        info.Spotify.Should().BeNull();
        info.Apple.Should().Be("GBUM70000002");
    }

    [Fact]
    public void Extract_WithSpotifyIsrcNotString_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": 12345 } },
            ""apple_music"": { ""isrc"": ""GBUM70000002"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000002");
        info.Spotify.Should().BeNull();
        info.Apple.Should().Be("GBUM70000002");
    }

    [Fact]
    public void Extract_WithAppleNotAnObject_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } },
            ""apple_music"": 123
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().BeNull();
    }

    [Fact]
    public void Extract_WithAppleIsrcNotString_HandlesGracefully()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } },
            ""apple_music"": { ""isrc"": null }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().BeNull();
    }

    [Fact]
    public void Extract_WithAllThreeSources_PrefersTopLevel()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""TOPLEVL001"",
            ""spotify"": { ""external_ids"": { ""isrc"": ""SPOTIFYRC"" } },
            ""apple_music"": { ""isrc"": ""APPLEISRC"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("TOPLEVL001");
        info.Spotify.Should().Be("SPOTIFYRC");
        info.Apple.Should().Be("APPLEISRC");
        info.Suffix.Should().Be("[ISRC: TOPLEVL001]");
    }

    [Fact]
    public void Extract_WithTopLevelAndSpotify_PrefersTopLevel()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""TOPLEVL001"",
            ""spotify"": { ""external_ids"": { ""isrc"": ""SPOTIFYRC"" } }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("TOPLEVL001");
        info.Spotify.Should().Be("SPOTIFYRC");
        info.Apple.Should().BeNull();
        info.Suffix.Should().Be("[ISRC: TOPLEVL001]");
    }

    [Fact]
    public void Extract_WithSpotifyAndApple_PrefersSpotify()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""SPOTIFYRC"" } },
            ""apple_music"": { ""isrc"": ""APPLEISRC"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("SPOTIFYRC");
        info.Spotify.Should().Be("SPOTIFYRC");
        info.Apple.Should().Be("APPLEISRC");
    }

    [Fact]
    public void Extract_WithCaseSensitiveMismatchComparison_IgnoresCase()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""spotify"": { ""external_ids"": { ""isrc"": ""GbUm70000001"" } },
            ""apple_music"": { ""isrc"": ""GBUM70000001"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
        info.Apple.Should().Be("GBUM70000001");
        info.Suffix.Should().Be("[ISRC: GBUM70000001]");
    }

    [Fact]
    public void Extract_WithTopLevelAndMismatchedSpotifyApple_PrefersTopLevelSuffix()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": ""TOPLEVL001"",
            ""spotify"": { ""external_ids"": { ""isrc"": ""SPOTIFYRC"" } },
            ""apple_music"": { ""isrc"": ""APPLEISRC"" }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("TOPLEVL001");
        info.Suffix.Should().Be("[ISRC: TOPLEVL001]");
    }

    [Fact]
    public void Extract_WithEmptyStringIsrc_TreatsAsNull()
    {
        // Arrange
        JsonElement result = JsonDocument.Parse(@"{
            ""isrc"": """",
            ""spotify"": { ""external_ids"": { ""isrc"": ""GBUM70000001"" } }
        }").RootElement;

        // Act
        IsrcInfo info = IsrcExtractor.Extract(result);

        // Assert
        info.Selected.Should().Be("GBUM70000001");
        info.Spotify.Should().Be("GBUM70000001");
    }
}
