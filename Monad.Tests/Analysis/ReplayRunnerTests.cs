using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad;
using Monad.Aggregation;
using Monad.Analysis;
using Monad.Audio;
using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Analysis;

/// <summary>
/// End-to-end coverage of the analysis pipeline: audio in, segments out, with
/// nothing faked but the recognition service itself.
/// </summary>
public sealed class ReplayRunnerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const int Rate = 100; // 100 Hz keeps the windows small and the maths obvious

    private static MonadConfig Config => new()
    {
        WindowSeconds = 1,
        IntervalSeconds = 1,
        BufferSeconds = 5,
        RingBufferMaxSamples = 1,
        RingBufferMaxAgeSeconds = 120,
        VoteWeightsNewestToOldest = [1.0],
        MinLeaderShare = 0.50,
        LeaderPersistLoops = 1,
        MinSegmentDurationSeconds = 0,
        SilenceEnterPersistLoops = 2,
        SilenceExitPersistLoops = 1,
        UnknownPersistLoops = 2,
    };

    /// <summary>Alternating ±amplitude, so RMS equals amplitude exactly.</summary>
    private static float[] Tone(double seconds, float amplitude)
    {
        float[] samples = new float[(int)(seconds * Rate)];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? amplitude : -amplitude;
        }

        return samples;
    }

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static Task<RunSummary> Run(
        float[] mono, ISongRecognizer recognizer, RecordingSegmentSink sink, MonadConfig? config = null) =>
        ReplayRunner.RunAsync(
            new DecodedAudio(mono, Rate),
            recognizer,
            sink,
            config ?? Config,
            T0,
            MonadLog.Silent,
            CancellationToken.None);

    [Fact]
    public async Task RunAsync_WithOneTrackThroughout_ReportsASingleSegment()
    {
        // Arrange — 10 seconds of audio, always recognised as the same track
        RecordingSegmentSink sink = new();
        FakeRecognizer recognizer = new(FakeRecognizer.Match("Nina Simone", "Feeling Good", "USSM17300123"));

        // Act
        RunSummary summary = await Run(Tone(10, 0.05f), recognizer, sink);

        // Assert
        summary.WindowsAnalyzed.Should().Be(10);
        summary.Lookups.Should().Be(10);

        sink.Segments.Should().ContainSingle();
        sink.Segments[0].Kind.Should().Be(SegmentKind.Song);
        sink.Segments[0].SongKey.Should().Be("isrc:USSM17300123");
        sink.Segments[0].DisplayText.Should().Contain("Nina Simone");
    }

    [Fact]
    public async Task RunAsync_WhenTheTrackChanges_ReportsBothInOrder()
    {
        // Arrange — the recognizer switches tracks after five windows
        RecordingSegmentSink sink = new();
        RecognitionOutcome first = FakeRecognizer.Match("Artist A", "Song A", "AAA111111111");
        RecognitionOutcome second = FakeRecognizer.Match("Artist B", "Song B", "BBB222222222");

        FakeRecognizer recognizer = new(first, first, first, first, first, second);

        // Act
        await Run(Tone(10, 0.05f), recognizer, sink);

        // Assert
        sink.Segments.Should().HaveCount(2);
        sink.Segments[0].SongKey.Should().Be("isrc:AAA111111111");
        sink.Segments[1].SongKey.Should().Be("isrc:BBB222222222");
    }

    [Fact]
    public async Task RunAsync_LeavesNoGapsBetweenConsecutiveSegments()
    {
        // Arrange
        RecordingSegmentSink sink = new();
        RecognitionOutcome first = FakeRecognizer.Match("A", "A", "AAA111111111");
        RecognitionOutcome second = FakeRecognizer.Match("B", "B", "BBB222222222");
        FakeRecognizer recognizer = new(first, first, first, second);

        // Act
        await Run(Tone(10, 0.05f), recognizer, sink);

        // Assert
        sink.Segments.Should().HaveCountGreaterThan(1);

        foreach ((Segment earlier, Segment later) in sink.Segments.Zip(sink.Segments.Skip(1)))
        {
            later.StartUtc.Should().Be(earlier.EndUtc);
        }
    }

    [Fact]
    public async Task RunAsync_WithSilentAudio_ReportsSilenceAndSpendsNoLookups()
    {
        // Arrange
        RecordingSegmentSink sink = new();
        FakeRecognizer recognizer = new();

        // Act
        RunSummary summary = await Run(Silence(10), recognizer, sink);

        // Assert
        summary.Lookups.Should().Be(0);
        recognizer.Calls.Should().Be(0);

        sink.Segments.Should().ContainSingle();
        sink.Segments[0].Kind.Should().Be(SegmentKind.Silence);
        sink.Segments[0].Label.Should().Be("SILENCE");
    }

    [Fact]
    public async Task RunAsync_AcrossMusicThenSilence_ReportsBoth()
    {
        // Arrange — six seconds of a track, then six seconds of nothing
        RecordingSegmentSink sink = new();
        FakeRecognizer recognizer = new(FakeRecognizer.Match("Artist", "Song", "AAA111111111"));

        float[] audio = [.. Tone(6, 0.05f), .. Silence(6)];

        // Act
        await Run(audio, recognizer, sink);

        // Assert
        sink.Segments.Select(segment => segment.Kind)
            .Should().ContainInOrder(SegmentKind.Song, SegmentKind.Silence);
    }

    [Fact]
    public async Task RunAsync_WithAudioNothingCanIdentify_ReportsUnknown()
    {
        // Arrange — what --no-recognize does, and what ads and speech look like
        RecordingSegmentSink sink = new();

        // Act
        await Run(Tone(10, 0.05f), new NullRecognizer(), sink);

        // Assert
        sink.Segments.Should().ContainSingle();
        sink.Segments[0].Kind.Should().Be(SegmentKind.Unknown);
    }

    [Fact]
    public async Task RunAsync_AlwaysReportsTheSegmentStillOpenAtTheEnd()
    {
        // Arrange
        RecordingSegmentSink sink = new();
        FakeRecognizer recognizer = new(FakeRecognizer.Match("Artist", "Song", "AAA111111111"));

        // Act
        await Run(Tone(10, 0.05f), recognizer, sink);

        // Assert — the run ends mid-track; that track still has to be reported
        sink.Segments.Should().ContainSingle();
        sink.Segments[0].EndUtc.Should().Be(T0.AddSeconds(10));
    }

    [Fact]
    public async Task RunAsync_HonoursTheMinimumSegmentDuration()
    {
        // Arrange — tracks change every window, so none lasts long enough
        RecordingSegmentSink sink = new();
        List<RecognitionOutcome> script = [];

        for (int i = 0; i < 12; i++)
        {
            script.Add(FakeRecognizer.Match($"Artist {i}", $"Song {i}", $"AAA{i:D9}"));
        }

        // Act
        await Run(Tone(12, 0.05f), new FakeRecognizer([.. script]), sink,
            Config with { MinSegmentDurationSeconds = 5 });

        // Assert
        sink.Segments.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WithAudioShorterThanOneWindow_AnalysesNothing()
    {
        // Arrange
        RecordingSegmentSink sink = new();
        FakeRecognizer recognizer = new();

        // Act
        RunSummary summary = await Run(Tone(0.5, 0.05f), recognizer, sink);

        // Assert
        summary.WindowsAnalyzed.Should().Be(0);
        sink.Segments.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WithAnInvalidConfiguration_Throws()
    {
        // Arrange
        MonadConfig broken = Config with { VoteWeightsNewestToOldest = [1.0, 0.5] };

        // Act / Assert
        await FluentActions
            .Awaiting(() => Run(Tone(5, 0.05f), new NullRecognizer(), new RecordingSegmentSink(), broken))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_Stops()
    {
        // Arrange
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act / Assert
        await FluentActions
            .Awaiting(() => ReplayRunner.RunAsync(
                new DecodedAudio(Tone(10, 0.05f), Rate),
                new NullRecognizer(),
                new RecordingSegmentSink(),
                Config,
                T0,
                MonadLog.Silent,
                cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
