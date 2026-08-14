using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Aggregation;
using Monad.Output;

using Xunit;

namespace Monad.Tests.Output;

public sealed class SegmentSinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static Segment Song => new(
        SegmentKind.Song,
        T0,
        T0.AddSeconds(185),
        "isrc:GB5EM1001054",
        "Isabelle Antena - Le Poisson Des Mers Du Sud [ISRC: GB5EM1001054]");

    private static Segment Silence => new(SegmentKind.Silence, T0, T0.AddSeconds(65), null, null);

    // --- Console ---

    [Fact]
    public async Task ConsoleSegmentSink_WritesOneLinePerSegment()
    {
        // Arrange
        StringWriter writer = new();
        await using ConsoleSegmentSink sink = new(writer);

        // Act
        await sink.EmitAsync(Song, CancellationToken.None);

        // Assert
        string line = writer.ToString().TrimEnd();
        line.Should().StartWith("SEGMENT").And.Contain("Isabelle Antena").And.Contain("3m05s");
    }

    [Fact]
    public void ConsoleSegmentSink_LabelsNonSongSegmentsByState()
    {
        ConsoleSegmentSink.Format(Silence).Should().Contain("SILENCE").And.Contain("1m05s");
    }

    [Fact]
    public void ConsoleSegmentSink_ShowsTimesInLocalTime()
    {
        // Arrange / Act
        string line = ConsoleSegmentSink.Format(Song);

        // Assert
        line.Should().Contain(T0.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    }

    // --- JSON Lines ---

    [Fact]
    public void JsonlSegmentSink_SerializesEveryFieldOfASong()
    {
        // Arrange / Act
        using JsonDocument document = JsonDocument.Parse(JsonlSegmentSink.Serialize(Song));
        JsonElement root = document.RootElement;

        // Assert
        root.GetProperty("kind").GetString().Should().Be("song");
        root.GetProperty("songKey").GetString().Should().Be("isrc:GB5EM1001054");
        root.GetProperty("display").GetString().Should().Contain("Isabelle Antena");
        root.GetProperty("durationSeconds").GetDouble().Should().Be(185);
        root.GetProperty("startUtc").GetString().Should().StartWith("2026-01-15T10:00:00");
    }

    [Fact]
    public void JsonlSegmentSink_OmitsFieldsASegmentDoesNotHave()
    {
        // Arrange / Act
        using JsonDocument document = JsonDocument.Parse(JsonlSegmentSink.Serialize(Silence));

        // Assert
        document.RootElement.TryGetProperty("songKey", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("display", out _).Should().BeFalse();
        document.RootElement.GetProperty("kind").GetString().Should().Be("silence");
    }

    [Fact]
    public void JsonlSegmentSink_ProducesExactlyOneLinePerSegment()
    {
        JsonlSegmentSink.Serialize(Song).Should().NotContain("\n");
    }

    [Fact]
    public async Task JsonlSegmentSink_AppendsToTheFileRatherThanReplacingIt()
    {
        // Arrange — a session may be stopped and restarted; the log must survive
        string path = Path.Combine(Path.GetTempPath(), $"monad-test-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (JsonlSegmentSink first = new(path))
            {
                await first.EmitAsync(Song, CancellationToken.None);
            }

            await using (JsonlSegmentSink second = new(path))
            {
                await second.EmitAsync(Silence, CancellationToken.None);
            }

            // Assert
            string[] lines = await File.ReadAllLinesAsync(path);
            lines.Should().HaveCount(2);
            lines[0].Should().Contain("\"song\"");
            lines[1].Should().Contain("\"silence\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JsonlSegmentSink_CreatesTheDirectoryItNeeds()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), $"monad-test-{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(directory, "segments.jsonl");

        try
        {
            // Act
            await using (JsonlSegmentSink sink = new(path))
            {
                await sink.EmitAsync(Song, CancellationToken.None);
            }

            // Assert
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [Fact]
    public async Task JsonlSegmentSink_ResolvesTheFullPath()
    {
        // Arrange — a relative path is reported back resolved, so the startup
        // line tells you exactly where the log is going.
        string path = $"monad-test-{Guid.NewGuid():N}.jsonl";

        try
        {
            // Act
            await using JsonlSegmentSink sink = new(path);

            // Assert
            sink.FilePath.Should().Be(Path.GetFullPath(path));
        }
        finally
        {
            File.Delete(Path.GetFullPath(path));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void JsonlSegmentSink_WithABlankPath_Throws(string path)
    {
        FluentActions.Invoking(() => new JsonlSegmentSink(path)).Should().Throw<ArgumentException>();
    }

    // --- Composite ---

    [Fact]
    public async Task CompositeSegmentSink_ForwardsToEverySink()
    {
        // Arrange
        RecordingSegmentSink one = new();
        RecordingSegmentSink two = new();
        await using CompositeSegmentSink composite = new([one, two]);

        // Act
        await composite.EmitAsync(Song, CancellationToken.None);

        // Assert
        one.Segments.Should().ContainSingle();
        two.Segments.Should().ContainSingle();
    }

    [Fact]
    public async Task CompositeSegmentSink_DisposesEverySink()
    {
        // Arrange
        RecordingSegmentSink one = new();
        RecordingSegmentSink two = new();
        CompositeSegmentSink composite = new([one, two]);

        // Act
        await composite.DisposeAsync();

        // Assert
        one.Disposed.Should().BeTrue();
        two.Disposed.Should().BeTrue();
    }
}
