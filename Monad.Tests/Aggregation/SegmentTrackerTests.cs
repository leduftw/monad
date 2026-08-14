using System;

using FluentAssertions;

using Monad;
using Monad.Aggregation;

using Xunit;

namespace Monad.Tests.Aggregation;

public sealed class SegmentTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const double Loud = -30.0;
    private const double Quiet = -70.0;

    /// <summary>One sample decides the leader outright, which keeps most tests to the point.</summary>
    private static MonadConfig Immediate => new()
    {
        RingBufferMaxSamples = 1,
        RingBufferMaxAgeSeconds = 300,
        VoteWeightsNewestToOldest = [1.0],
        MinLeaderShare = 0.50,
        LeaderPersistLoops = 1,
        MinSegmentDurationSeconds = 0,
        SilenceEnterPersistLoops = 2,
        SilenceExitPersistLoops = 2,
        UnknownPersistLoops = 2,
    };

    private static MonadConfig Voting => new()
    {
        RingBufferMaxSamples = 3,
        RingBufferMaxAgeSeconds = 300,
        VoteWeightsNewestToOldest = [1.0, 1.0, 1.0],
        MinLeaderShare = 0.40,
        LeaderPersistLoops = 2,
        MinSegmentDurationSeconds = 0,
        SilenceEnterPersistLoops = 2,
        SilenceExitPersistLoops = 2,
        UnknownPersistLoops = 99, // keep Unknown out of the way
    };

    private static RecognitionSample Sample(
        DateTimeOffset at, double dbfs, string? key = null, string? display = null) =>
        new(at, dbfs, key, IsNoMatch: key is null, display);

    // --- Silence ---

    [Fact]
    public void OnSample_WithSilenceForEnterPersistLoops_EntersSilence()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act
        tracker.OnSample(Sample(T0, Quiet));
        SegmentKind? afterOne = tracker.ActiveKind;

        tracker.OnSample(Sample(T0.AddSeconds(10), Quiet));

        // Assert — one quiet window is not enough, two are
        afterOne.Should().NotBe(SegmentKind.Silence);
        tracker.ActiveKind.Should().Be(SegmentKind.Silence);
    }

    [Fact]
    public void OnSample_WithASingleQuietWindow_DoesNotEnterSilence()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act
        tracker.OnSample(Sample(T0, Quiet));

        // Assert
        tracker.ActiveKind.Should().NotBe(SegmentKind.Silence);
    }

    [Fact]
    public void OnSample_InSilence_StaysUntilLoudAudioPersists()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Quiet));
        tracker.OnSample(Sample(T0.AddSeconds(10), Quiet));

        // Act — a single loud window is not enough to leave
        tracker.OnSample(Sample(T0.AddSeconds(20), Loud, "isrc:AAA", "Song A"));
        SegmentKind? afterOne = tracker.ActiveKind;

        tracker.OnSample(Sample(T0.AddSeconds(30), Loud, "isrc:AAA", "Song A"));

        // Assert
        afterOne.Should().Be(SegmentKind.Silence);
        tracker.ActiveKind.Should().Be(SegmentKind.Song);
    }

    [Fact]
    public void OnSample_InSilence_StaysThereWhileItRemainsQuiet()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Quiet));
        tracker.OnSample(Sample(T0.AddSeconds(10), Quiet));

        // Act
        Segment? result = tracker.OnSample(Sample(T0.AddSeconds(20), Quiet));

        // Assert
        result.Should().BeNull();
        tracker.ActiveKind.Should().Be(SegmentKind.Silence);
        tracker.ActiveStartUtc.Should().Be(T0.AddSeconds(10));
    }

    [Fact]
    public void OnSample_LevelBetweenTheTwoThresholds_DoesNotFlapOutOfSilence()
    {
        // Arrange — the gap between enter (-65) and exit (-60) is the hysteresis
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Quiet));
        tracker.OnSample(Sample(T0.AddSeconds(10), Quiet));

        // Act — -62 dBFS sits in the dead band
        tracker.OnSample(Sample(T0.AddSeconds(20), -62.0));
        tracker.OnSample(Sample(T0.AddSeconds(30), -62.0));

        // Assert
        tracker.ActiveKind.Should().Be(SegmentKind.Silence);
    }

    [Fact]
    public void OnSample_SilenceEndingInASong_FinalizesTheSilenceSegment()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Quiet));
        tracker.OnSample(Sample(T0.AddSeconds(10), Quiet)); // silence starts here

        tracker.OnSample(Sample(T0.AddSeconds(20), Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? finalized = tracker.OnSample(Sample(T0.AddSeconds(30), Loud, "isrc:AAA", "Song A"));

        // Assert
        finalized.Should().NotBeNull();
        finalized!.Kind.Should().Be(SegmentKind.Silence);
        finalized.StartUtc.Should().Be(T0.AddSeconds(10));
        finalized.Label.Should().Be("SILENCE");
    }

    // --- Songs ---

    [Fact]
    public void OnSample_WithAnAcceptedLeader_OpensASongSegment()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act
        Segment? result = tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Assert — nothing to finalize yet, but the state has moved
        result.Should().BeNull();
        tracker.ActiveKind.Should().Be(SegmentKind.Song);
        tracker.ActiveSongKey.Should().Be("isrc:AAA");
    }

    [Fact]
    public void OnSample_WithASongTransition_FinalizesThePreviousSong()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? result = tracker.OnSample(Sample(T0.AddSeconds(60), Loud, "isrc:BBB", "Song B"));

        // Assert
        result.Should().NotBeNull();
        result!.Kind.Should().Be(SegmentKind.Song);
        result.SongKey.Should().Be("isrc:AAA");
        result.DisplayText.Should().Be("Song A");
        result.Duration.Should().Be(TimeSpan.FromSeconds(60));
        tracker.ActiveSongKey.Should().Be("isrc:BBB");
    }

    [Fact]
    public void OnSample_AcrossATransition_LeavesNoGapInTheTimeline()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? finalized = tracker.OnSample(Sample(T0.AddSeconds(60), Loud, "isrc:BBB", "Song B"));

        // Assert — the old segment ends exactly where the new one begins
        finalized!.EndUtc.Should().Be(tracker.ActiveStartUtc!.Value);
    }

    [Fact]
    public void OnSample_WithTheSameSongAgain_DoesNotStartANewSegment()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? result = tracker.OnSample(Sample(T0.AddSeconds(20), Loud, "isrc:AAA", "Song A"));

        // Assert
        result.Should().BeNull();
        tracker.ActiveStartUtc.Should().Be(T0);
    }

    [Fact]
    public void OnSample_WithALeaderBelowMinShare_DoesNotOpenASongSegment()
    {
        // Arrange — two unrecognised windows dilute the one match to 1/3
        MonadConfig config = Voting with { MinLeaderShare = 0.60, LeaderPersistLoops = 1 };
        SegmentTracker tracker = new(config);

        tracker.OnSample(Sample(T0, Loud));
        tracker.OnSample(Sample(T0.AddSeconds(10), Loud));

        // Act
        tracker.OnSample(Sample(T0.AddSeconds(20), Loud, "isrc:AAA", "Song A"));

        // Assert
        tracker.ActiveKind.Should().NotBe(SegmentKind.Song);
    }

    [Fact]
    public void OnSample_DatesTheSegmentFromWhenTheTrackWasFirstHeard()
    {
        // Arrange — recognition needs two loops to settle, but the song was
        // already playing during the first one.
        SegmentTracker tracker = new(Voting);

        // Act
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));
        tracker.OnSample(Sample(T0.AddSeconds(15), Loud, "isrc:AAA", "Song A"));

        // Assert — dated from first contact, not from when the vote settled
        tracker.ActiveKind.Should().Be(SegmentKind.Song);
        tracker.ActiveStartUtc.Should().Be(T0);
    }

    [Fact]
    public void OnSample_WhenTheDecidingWindowRecognisedNothing_StillNamesTheSegment()
    {
        // Arrange — the vote elects a track from earlier windows while the
        // current one came back empty, which is entirely routine. The name has
        // to survive that, or the segment is reported with no title at all.
        SegmentTracker tracker = new(Voting);

        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Artist - Title [ISRC: AAA]"));
        tracker.OnSample(Sample(T0.AddSeconds(15), Loud)); // no match: elects AAA anyway

        tracker.ActiveKind.Should().Be(SegmentKind.Song);
        tracker.ActiveSongKey.Should().Be("isrc:AAA");

        // Act — play another track until it takes over and closes the first
        Segment? finalized = null;

        for (int i = 2; i <= 5 && finalized is null; i++)
        {
            finalized = tracker.OnSample(Sample(T0.AddSeconds(15 * i), Loud, "isrc:BBB", "Other"));
        }

        // Assert
        finalized.Should().NotBeNull();
        finalized!.SongKey.Should().Be("isrc:AAA");
        finalized.DisplayText.Should().Be("Artist - Title [ISRC: AAA]");
        finalized.Label.Should().Be("Artist - Title [ISRC: AAA]");
    }

    // --- Unknown ---

    [Fact]
    public void OnSample_WithLoudButUnrecognisableAudio_BecomesUnknown()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act
        tracker.OnSample(Sample(T0, Loud));
        SegmentKind? afterOne = tracker.ActiveKind;

        tracker.OnSample(Sample(T0.AddSeconds(10), Loud));

        // Assert
        afterOne.Should().NotBe(SegmentKind.Unknown);
        tracker.ActiveKind.Should().Be(SegmentKind.Unknown);
    }

    [Fact]
    public void OnSample_WithAudioTooQuietToCountAsPlaying_DoesNotBecomeUnknown()
    {
        // Arrange — below UnknownMinDbfs but above the silence threshold
        SegmentTracker tracker = new(Immediate);

        // Act
        tracker.OnSample(Sample(T0, -62.0));
        tracker.OnSample(Sample(T0.AddSeconds(10), -62.0));

        // Assert
        tracker.ActiveKind.Should().BeNull();
    }

    // --- Finalization ---

    [Fact]
    public void OnSample_WithASegmentShorterThanTheMinimum_DropsIt()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate with { MinSegmentDurationSeconds = 30 });
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act — only five seconds later
        Segment? result = tracker.OnSample(Sample(T0.AddSeconds(5), Loud, "isrc:BBB", "Song B"));

        // Assert — dropped, but the tracker still moved on
        result.Should().BeNull();
        tracker.ActiveSongKey.Should().Be("isrc:BBB");
    }

    [Fact]
    public void OnSample_WithASegmentMeetingTheMinimum_ReportsIt()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate with { MinSegmentDurationSeconds = 30 });
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? result = tracker.OnSample(Sample(T0.AddSeconds(45), Loud, "isrc:BBB", "Song B"));

        // Assert
        result.Should().NotBeNull();
        result!.StartUtc.Should().Be(T0);
        result.EndUtc.Should().Be(T0.AddSeconds(45));
    }

    // --- Flush ---

    [Fact]
    public void Flush_ReportsTheSegmentStillInProgress()
    {
        // Arrange — without this, whatever was playing when you stopped Monad
        // was simply never reported.
        SegmentTracker tracker = new(Immediate);
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act
        Segment? last = tracker.Flush(T0.AddSeconds(90));

        // Assert
        last.Should().NotBeNull();
        last!.SongKey.Should().Be("isrc:AAA");
        last.DisplayText.Should().Be("Song A");
        last.EndUtc.Should().Be(T0.AddSeconds(90));
        tracker.ActiveKind.Should().BeNull();
    }

    [Fact]
    public void Flush_WithNothingInProgress_ReturnsNull()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act / Assert
        tracker.Flush(T0).Should().BeNull();
    }

    [Fact]
    public void Flush_HonoursTheMinimumDuration()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate with { MinSegmentDurationSeconds = 30 });
        tracker.OnSample(Sample(T0, Loud, "isrc:AAA", "Song A"));

        // Act / Assert
        tracker.Flush(T0.AddSeconds(5)).Should().BeNull();
    }

    [Fact]
    public void OnSample_WithNullSample_Throws()
    {
        // Arrange
        SegmentTracker tracker = new(Immediate);

        // Act / Assert
        FluentActions.Invoking(() => tracker.OnSample(null!)).Should().Throw<ArgumentNullException>();
    }
}
