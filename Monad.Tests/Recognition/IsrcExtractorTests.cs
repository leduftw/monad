using System.Text.Json;

using FluentAssertions;

using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Recognition;

public sealed class IsrcExtractorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // --- Selection ---

    [Fact]
    public void Extract_WithATopLevelIsrc_SelectsIt()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse("""{"isrc":"USS1Z2600001"}"""));

        // Assert
        info.Selected.Should().Be("USS1Z2600001");
        info.TopLevel.Should().Be("USS1Z2600001");
        info.Suffix.Should().Be("[ISRC: USS1Z2600001]");
        info.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void Extract_WithOnlySpotify_SelectsSpotify()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(
            Parse("""{"spotify":{"external_ids":{"isrc":"USS1Z2600002"}}}"""));

        // Assert
        info.Selected.Should().Be("USS1Z2600002");
        info.Spotify.Should().Be("USS1Z2600002");
        info.Suffix.Should().Be("[ISRC: USS1Z2600002]");
    }

    [Fact]
    public void Extract_WithOnlyAppleMusic_SelectsApple()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse("""{"apple_music":{"isrc":"USS1Z2600003"}}"""));

        // Assert
        info.Selected.Should().Be("USS1Z2600003");
        info.Apple.Should().Be("USS1Z2600003");
    }

    [Fact]
    public void Extract_PrefersTheTopLevelCodeOverTheStores()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse(
            """{"isrc":"AAA111111111","spotify":{"external_ids":{"isrc":"AAA111111111"}},"apple_music":{"isrc":"AAA111111111"}}"""));

        // Assert
        info.Selected.Should().Be("AAA111111111");
        info.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void Extract_PrefersSpotifyOverAppleWhenThereIsNoTopLevelCode()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse(
            """{"spotify":{"external_ids":{"isrc":"SPO111111111"}},"apple_music":{"isrc":"SPO111111111"}}"""));

        // Assert
        info.Selected.Should().Be("SPO111111111");
    }

    [Fact]
    public void Extract_UppercasesWhateverItFinds()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse("""{"isrc":"uss1z2600001"}"""));

        // Assert
        info.Selected.Should().Be("USS1Z2600001");
    }

    [Fact]
    public void Extract_WithNoIsrcAnywhere_ReportsNone()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse("""{"artist":"Someone","title":"Something"}"""));

        // Assert
        info.Selected.Should().BeNull();
        info.Suffix.Should().Be("[ISRC: n/a]");
        info.HasMismatch.Should().BeFalse();
    }

    // --- Mismatch detection ---

    [Fact]
    public void Extract_WhenSpotifyAndAppleDisagree_ReportsAMismatch()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse(
            """{"spotify":{"external_ids":{"isrc":"AAA111111111"}},"apple_music":{"isrc":"BBB222222222"}}"""));

        // Assert
        info.HasMismatch.Should().BeTrue();
        info.Suffix.Should().Contain("mismatch").And.Contain("AAA111111111").And.Contain("BBB222222222");
    }

    [Fact]
    public void Extract_WhenTheTopLevelCodeDisagreesWithAStore_ReportsAMismatch()
    {
        // Arrange — this used to go unnoticed: a top-level code short-circuited
        // the comparison, so the disagreement was never looked for.
        IsrcInfo info = IsrcExtractor.Extract(Parse(
            """{"isrc":"AAA111111111","spotify":{"external_ids":{"isrc":"BBB222222222"}}}"""));

        // Assert
        info.HasMismatch.Should().BeTrue();
        info.Selected.Should().Be("AAA111111111"); // still prefers the top-level code
        info.Suffix.Should().Contain("result=AAA111111111").And.Contain("spotify=BBB222222222");
    }

    [Fact]
    public void Extract_WithCodesDifferingOnlyInCase_DoesNotReportAMismatch()
    {
        // Arrange / Act
        IsrcInfo info = IsrcExtractor.Extract(Parse(
            """{"spotify":{"external_ids":{"isrc":"AAA111111111"}},"apple_music":{"isrc":"aaa111111111"}}"""));

        // Assert
        info.HasMismatch.Should().BeFalse();
        info.Suffix.Should().Be("[ISRC: AAA111111111]");
    }

    // --- Malformed input ---

    [Theory]
    [InlineData("""{"isrc":12345}""")]
    [InlineData("""{"isrc":true}""")]
    [InlineData("""{"isrc":null}""")]
    [InlineData("""{"isrc":{"value":"AAA"}}""")]
    [InlineData("""{"isrc":["AAA"]}""")]
    public void Extract_WithANonStringIsrc_DoesNotThrow(string json)
    {
        // Arrange — a field of the wrong type used to take the whole
        // recognition loop down with an InvalidOperationException.
        IsrcInfo info = IsrcExtractor.Extract(Parse(json));

        // Assert
        info.Selected.Should().BeNull();
        info.Suffix.Should().Be("[ISRC: n/a]");
    }

    [Theory]
    [InlineData("""{"spotify":"not-an-object"}""")]
    [InlineData("""{"spotify":{"external_ids":"not-an-object"}}""")]
    [InlineData("""{"spotify":{"external_ids":{"isrc":42}}}""")]
    [InlineData("""{"spotify":{}}""")]
    [InlineData("""{"apple_music":"not-an-object"}""")]
    [InlineData("""{"apple_music":{"isrc":42}}""")]
    public void Extract_WithMalformedStoreData_DoesNotThrow(string json)
    {
        IsrcExtractor.Extract(Parse(json)).Selected.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"isrc":""}""")]
    [InlineData("""{"isrc":"   "}""")]
    public void Extract_WithABlankIsrc_TreatsItAsAbsent(string json)
    {
        IsrcExtractor.Extract(Parse(json)).Selected.Should().BeNull();
    }

    [Fact]
    public void Extract_TrimsSurroundingWhitespace()
    {
        IsrcExtractor.Extract(Parse("""{"isrc":"  USS1Z2600001  "}""")).Selected.Should().Be("USS1Z2600001");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Extract_WithSomethingOtherThanAnObject_ReportsNone(string json)
    {
        IsrcExtractor.Extract(Parse(json)).Should().BeSameAs(IsrcInfo.None);
    }
}
