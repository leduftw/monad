using System;

using FluentAssertions;

using Monad;

using Xunit;

namespace Monad.Tests;

public sealed class MonadConfigTests
{
    [Fact]
    public void Validate_WithTheDefaults_Passes()
    {
        FluentActions.Invoking(() => new MonadConfig().Validate()).Should().NotThrow();
    }

    [Fact]
    public void Defaults_KeepTheVotingWindowInsideItsAgeLimit()
    {
        // Arrange — five samples fifteen seconds apart is 75s, comfortably
        // inside the 120s limit, so votes never expire unpredictably.
        MonadConfig config = new();

        // Act / Assert
        (config.RingBufferMaxSamples * config.IntervalSeconds)
            .Should().BeLessThanOrEqualTo(config.RingBufferMaxAgeSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_RejectsANonPositiveWindow(int seconds)
    {
        Assert(new MonadConfig { WindowSeconds = seconds, BufferSeconds = 30 }, "windowSeconds");
    }

    [Fact]
    public void Validate_RejectsANonPositiveInterval()
    {
        Assert(new MonadConfig { IntervalSeconds = 0 }, "intervalSeconds");
    }

    [Fact]
    public void Validate_RejectsABufferShorterThanTheWindow()
    {
        // Arrange — the buffer has to be able to hold a whole window
        Assert(new MonadConfig { WindowSeconds = 60, BufferSeconds = 30 }, "bufferSeconds");
    }

    [Fact]
    public void Validate_RejectsWeightsThatDoNotMatchTheWindowSize()
    {
        Assert(
            new MonadConfig { RingBufferMaxSamples = 3, VoteWeightsNewestToOldest = [1.0, 0.5] },
            "voteWeightsNewestToOldest");
    }

    [Fact]
    public void Validate_RejectsNegativeWeights()
    {
        Assert(
            new MonadConfig { RingBufferMaxSamples = 2, VoteWeightsNewestToOldest = [1.0, -0.5] },
            "non-negative");
    }

    [Fact]
    public void Validate_RejectsWeightsThatAreAllZero()
    {
        Assert(
            new MonadConfig { RingBufferMaxSamples = 2, VoteWeightsNewestToOldest = [0.0, 0.0] },
            "positive weight");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-0.2)]
    public void Validate_RejectsAnImpossibleLeaderShare(double share)
    {
        Assert(new MonadConfig { MinLeaderShare = share }, "minLeaderShare");
    }

    [Fact]
    public void Validate_RejectsInvertedSilenceThresholds()
    {
        // Arrange — the exit threshold must sit above the enter threshold, or
        // there is no hysteresis and the state machine flaps.
        Assert(
            new MonadConfig { SilenceEnterDbfsThreshold = -50.0, SilenceExitDbfsThreshold = -70.0 },
            "hysteresis");
    }

    [Fact]
    public void Validate_RejectsAVotingWindowThatWouldExpireBeforeItFills()
    {
        Assert(
            new MonadConfig { IntervalSeconds = 60, RingBufferMaxAgeSeconds = 90 },
            "ringBufferMaxAgeSeconds");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    public void Validate_RejectsAnImpossibleNormalizationTarget(float target)
    {
        Assert(new MonadConfig { NormalizePeakTarget = target }, "normalizePeakTarget");
    }

    [Fact]
    public void Validate_RejectsANonPositiveRetryCount()
    {
        Assert(new MonadConfig { MaxRecognitionAttempts = 0 }, "maxRecognitionAttempts");
    }

    [Fact]
    public void Validate_RejectsANegativeMinimumSegmentDuration()
    {
        Assert(new MonadConfig { MinSegmentDurationSeconds = -1 }, "minSegmentDurationSeconds");
    }

    [Fact]
    public void WindowAndInterval_ExposeTheirSecondsAsTimeSpans()
    {
        // Arrange
        MonadConfig config = new() { WindowSeconds = 12, IntervalSeconds = 15 };

        // Act / Assert
        config.Window.Should().Be(TimeSpan.FromSeconds(12));
        config.Interval.Should().Be(TimeSpan.FromSeconds(15));
    }

    private static void Assert(MonadConfig config, string expectedInMessage) =>
        FluentActions.Invoking(config.Validate)
            .Should().Throw<ArgumentException>()
            .WithMessage($"*{expectedInMessage}*");
}
