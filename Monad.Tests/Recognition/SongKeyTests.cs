using FluentAssertions;
using Monad.Recognition;
using Xunit;

namespace Monad.Tests.Recognition;

public sealed class SongKeyTests
{
    [Fact]
    public void FromResult_WithValidIsrc_ReturnsIsrcFormattedKey()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: "US1234567890", Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Artist", Title: "Title", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("isrc:US1234567890");
    }

    [Fact]
    public void FromResult_WithIsrcContainingWhitespace_TrimsAndUppercases()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: "  us1234567890  ", Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Artist", Title: "Title", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("isrc:US1234567890");
    }

    [Fact]
    public void FromResult_WithNullIsrc_UsesArtistAndTitle()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Grace Jones", Title: "I've Done It Again", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:GRACE JONES|I'VE DONE IT AGAIN");
    }

    [Fact]
    public void FromResult_WithWhitespaceOnlyIsrc_UsesArtistAndTitle()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: "   ", Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Pink Floyd", Title: "Time", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:PINK FLOYD|TIME");
    }

    [Fact]
    public void FromResult_WithArtistAndTitleWhitespace_TrimsAndUppercases()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "  Depeche Mode  ", Title: "  Lie to Me  ", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:DEPECHE MODE|LIE TO ME");
    }

    [Fact]
    public void FromResult_WithEmptyArtistAndTitle_ReturnsNull()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "", Title: "", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().BeNull();
    }

    [Fact]
    public void FromResult_WithNullArtistAndTitle_ReturnsNull()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: null!, Title: null!, IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().BeNull();
    }

    [Fact]
    public void FromResult_WithWhitespaceOnlyArtistAndTitle_ReturnsNull()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "   ", Title: "   ", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().BeNull();
    }

    [Fact]
    public void FromResult_WithOnlyArtist_ReturnsFormattedKey()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Eartha Kitt", Title: "", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:EARTHA KITT|");
    }

    [Fact]
    public void FromResult_WithOnlyTitle_ReturnsFormattedKey()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "", Title: "Je Cherche Un Homme", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:|JE CHERCHE UN HOMME");
    }

    [Fact]
    public void FromResult_IsrcPrefersOverArtistAndTitle()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: "GBUM70000001", Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Some Artist", Title: "Some Title", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("isrc:GBUM70000001");
        songKey.Should().NotContain("Some Artist");
        songKey.Should().NotContain("Some Title");
    }

    [Fact]
    public void FromResult_WithMixedCase_UppercasesEverything()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: "UsaBc1234567", Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "artist", Title: "title", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("isrc:USABC1234567");
    }

    [Fact]
    public void FromResult_WithSpecialCharacters_PreservesInFormattedKey()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "AC/DC", Title: "Back in Black", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:AC/DC|BACK IN BLACK");
    }

    [Fact]
    public void FromResult_WithCyrillicCharacters_HandlesNonLatinScripts()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Молчат Дома", Title: "Судно (Борис Рижий)", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:МОЛЧАТ ДОМА|СУДНО (БОРИС РИЖИЙ)");
    }

    [Fact]
    public void FromResult_WithLatinDiacriticalMarks_HandlesSerbianCharacters()
    {
        // Arrange
        IsrcInfo isrcInfo = new(Selected: null, Spotify: null, Apple: null, Suffix: "");
        RecognitionResult result = new(Artist: "Zdravko Čolić", Title: "Ao nono bijela", IsrcInfo: isrcInfo);

        // Act
        string? songKey = SongKey.FromResult(result);

        // Assert
        songKey.Should().Be("text:ZDRAVKO ČOLIĆ|AO NONO BIJELA");
    }
}
