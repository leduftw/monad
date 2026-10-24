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
        SongKey.FromResult(Result("Artist", "Title", "USS1Z2600001"))
            .Should().Be("isrc:USS1Z2600001");
    }

    [Fact]
    public void FromResult_PrefersIsrcOverArtistAndTitle()
    {
        // Arrange — the same recording under two spellings must share a key
        string? one = SongKey.FromResult(Result("Artist A & Artist B", "Song A", "USS1Z2600001"));
        string? other = SongKey.FromResult(Result("Artist A and Artist B", "Song A (Remastered)", "USS1Z2600001"));

        // Assert
        one.Should().Be(other);
    }

    [Fact]
    public void FromResult_UppercasesAndTrimsTheIsrc()
    {
        SongKey.FromResult(Result("Artist", "Title", "  uss1z2600001  "))
            .Should().Be("isrc:USS1Z2600001");
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
        SongKey.FromResult(Result("Artist A", "Song A"))
            .Should().Be("text:ARTIST A|SONG A");
    }

    [Fact]
    public void FromResult_UppercasesTextKeys()
    {
        // Arrange — matching is case-insensitive, so the key is normalised once
        SongKey.FromResult(Result("artist a", "song a"))
            .Should().Be("text:ARTIST A|SONG A");
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
        SongKey.FromResult(Result("Исполнитель", "Песня"))
            .Should().Be("text:ИСПОЛНИТЕЛЬ|ПЕСНЯ");
    }

    [Fact]
    public void FromResult_HandlesDiacritics()
    {
        SongKey.FromResult(Result("Intérprete", "Canción"))
            .Should().Be("text:INTÉRPRETE|CANCIÓN");
    }

    [Fact]
    public void FromResult_KeepsPunctuationInTextKeys()
    {
        SongKey.FromResult(Result("A/B", "C.D.E. (Live)"))
            .Should().Be("text:A/B|C.D.E. (LIVE)");
    }

    [Fact]
    public void FromResult_WithNull_Throws()
    {
        FluentActions.Invoking(() => SongKey.FromResult(null!)).Should().Throw<ArgumentNullException>();
    }
}
