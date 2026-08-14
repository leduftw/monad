using System;

using FluentAssertions;

using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Recognition;

public sealed class SongKeyTests
{
    private static RecognitionResult Result(string artist, string title, string? isrc = null) =>
        new(artist, title, isrc is null ? IsrcInfo.None : IsrcInfo.None with { Selected = isrc });

    // --- ISRC keys ---

    [Fact]
    public void FromResult_WithAnIsrc_BuildsAnIsrcKey()
    {
        SongKey.FromResult(Result("Artist", "Title", "GBAYE0601498"))
            .Should().Be("isrc:GBAYE0601498");
    }

    [Fact]
    public void FromResult_PrefersIsrcOverArtistAndTitle()
    {
        // Arrange — the same recording under two spellings must share a key
        string? one = SongKey.FromResult(Result("The Beatles", "Hey Jude", "GBAYE0601498"));
        string? other = SongKey.FromResult(Result("Beatles, The", "Hey Jude (Remastered)", "GBAYE0601498"));

        // Assert
        one.Should().Be(other);
    }

    [Fact]
    public void FromResult_UppercasesAndTrimsTheIsrc()
    {
        SongKey.FromResult(Result("Artist", "Title", "  gbaye0601498  "))
            .Should().Be("isrc:GBAYE0601498");
    }

    [Fact]
    public void FromResult_WithABlankIsrc_FallsBackToArtistAndTitle()
    {
        SongKey.FromResult(Result("Artist", "Title", "   "))
            .Should().Be("text:ARTIST|TITLE");
    }

    // --- Text keys ---

    [Fact]
    public void FromResult_WithoutAnIsrc_BuildsATextKey()
    {
        SongKey.FromResult(Result("Nina Simone", "Feeling Good"))
            .Should().Be("text:NINA SIMONE|FEELING GOOD");
    }

    [Fact]
    public void FromResult_UppercasesTextKeys()
    {
        // Arrange — matching is case-insensitive, so the key is normalised once
        SongKey.FromResult(Result("nina simone", "feeling good"))
            .Should().Be("text:NINA SIMONE|FEELING GOOD");
    }

    [Fact]
    public void FromResult_TrimsArtistAndTitle()
    {
        SongKey.FromResult(Result("  Artist  ", "  Title  "))
            .Should().Be("text:ARTIST|TITLE");
    }

    [Fact]
    public void FromResult_WithOnlyAnArtist_StillBuildsAKey()
    {
        SongKey.FromResult(Result("Artist", string.Empty)).Should().Be("text:ARTIST|");
    }

    [Fact]
    public void FromResult_WithOnlyATitle_StillBuildsAKey()
    {
        SongKey.FromResult(Result(string.Empty, "Title")).Should().Be("text:|TITLE");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void FromResult_WithNothingToIdentifyIt_ReturnsNull(string artist, string title)
    {
        SongKey.FromResult(Result(artist, title)).Should().BeNull();
    }

    // --- Scripts and punctuation ---

    [Fact]
    public void FromResult_HandlesCyrillic()
    {
        SongKey.FromResult(Result("Бијело Дугме", "Ђурђевдан"))
            .Should().Be("text:БИЈЕЛО ДУГМЕ|ЂУРЂЕВДАН");
    }

    [Fact]
    public void FromResult_HandlesSerbianDiacritics()
    {
        SongKey.FromResult(Result("Đorđe Balašević", "Računajte Na Nas"))
            .Should().Be("text:ĐORĐE BALAŠEVIĆ|RAČUNAJTE NA NAS");
    }

    [Fact]
    public void FromResult_KeepsPunctuationInTextKeys()
    {
        SongKey.FromResult(Result("AC/DC", "T.N.T. (Live)"))
            .Should().Be("text:AC/DC|T.N.T. (LIVE)");
    }

    [Fact]
    public void FromResult_WithNull_Throws()
    {
        FluentActions.Invoking(() => SongKey.FromResult(null!)).Should().Throw<ArgumentNullException>();
    }
}
