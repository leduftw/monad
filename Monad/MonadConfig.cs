using System;
using System.Linq;

namespace Monad;

/// <summary>
/// Everything tunable about the monitoring loop. Defaults are chosen for
/// background music on a desktop; override them on the command line or in a
/// config file rather than editing this file.
/// </summary>
public sealed record class MonadConfig
{
    // Sampling ------------------------------------------------------------

    /// <summary>How much recent audio each recognition attempt looks at.</summary>
    public int WindowSeconds { get; init; } = 12;

    /// <summary>
    /// How often a window is analysed. Each tick is one API call at most, so
    /// this is the main lever on both responsiveness and cost.
    /// </summary>
    public int IntervalSeconds { get; init; } = 15;

    /// <summary>
    /// How much audio is kept in memory. Slightly longer than the window so a
    /// late-arriving block never leaves a hole at the start of one.
    /// </summary>
    public int BufferSeconds { get; init; } = 30;

    /// <summary>Sample rate assumed before the capture source reports its own.</summary>
    public int PreferredSampleRate { get; init; } = 48_000;

    /// <summary>Peak level clips are lifted to before being sent for recognition.</summary>
    public float NormalizePeakTarget { get; init; } = 0.90f;

    // Voting window -------------------------------------------------------

    public int RingBufferMaxSamples { get; init; } = 5;

    public int RingBufferMaxAgeSeconds { get; init; } = 120;

    /// <summary>Vote weights from newest to oldest; must be as long as <see cref="RingBufferMaxSamples"/>.</summary>
    public double[] VoteWeightsNewestToOldest { get; init; } = [1.0, 0.8, 0.6, 0.4, 0.2];

    // Leader election -----------------------------------------------------

    /// <summary>Share of total vote weight a track needs before it can be reported.</summary>
    public double MinLeaderShare { get; init; } = 0.60;

    /// <summary>Consecutive ticks a track must lead before a segment opens.</summary>
    public int LeaderPersistLoops { get; init; } = 2;

    // Segments ------------------------------------------------------------

    /// <summary>Segments shorter than this are dropped rather than reported.</summary>
    public int MinSegmentDurationSeconds { get; init; } = 30;

    // Silence -------------------------------------------------------------

    /// <summary>At or below this level, a window counts as silent. Also the level below which no lookup is made.</summary>
    public double SilenceEnterDbfsThreshold { get; init; } = -65.0;

    public int SilenceEnterPersistLoops { get; init; } = 2;

    /// <summary>Above this level, silence ends. Deliberately higher than the enter threshold, to stop flapping.</summary>
    public double SilenceExitDbfsThreshold { get; init; } = -60.0;

    public int SilenceExitPersistLoops { get; init; } = 1;

    // Unknown -------------------------------------------------------------

    /// <summary>Audio must be at least this loud to count as "something is playing but we cannot name it".</summary>
    public double UnknownMinDbfs { get; init; } = -60.0;

    public int UnknownPersistLoops { get; init; } = 2;

    /// <summary>Share of recent windows that must be unrecognised to call the audio scattered.</summary>
    public double UnknownNoMatchShare { get; init; } = 0.60;

    /// <summary>Number of competing tracks in the window that also counts as scattered.</summary>
    public int UnknownMinDistinctKeys { get; init; } = 2;

    // Recognition ---------------------------------------------------------

    /// <summary>Attempts per window, including the first, when failures look transient.</summary>
    public int MaxRecognitionAttempts { get; init; } = 3;

    /// <summary>Consecutive unrecoverable failures -- a rejected token, say -- before giving up.</summary>
    public int MaxConsecutiveFatalFailures { get; init; } = 3;

    public TimeSpan Window => TimeSpan.FromSeconds(this.WindowSeconds);

    public TimeSpan Interval => TimeSpan.FromSeconds(this.IntervalSeconds);

    /// <summary>
    /// Checks the combinations that only fail at runtime, so a bad config file
    /// is reported clearly at startup instead of throwing mid-loop.
    /// </summary>
    public void Validate()
    {
        Require(this.WindowSeconds > 0, "windowSeconds must be greater than zero.");
        Require(this.IntervalSeconds > 0, "intervalSeconds must be greater than zero.");
        Require(this.PreferredSampleRate > 0, "preferredSampleRate must be greater than zero.");
        Require(this.RingBufferMaxSamples > 0, "ringBufferMaxSamples must be greater than zero.");
        Require(this.RingBufferMaxAgeSeconds > 0, "ringBufferMaxAgeSeconds must be greater than zero.");
        Require(this.MinSegmentDurationSeconds >= 0, "minSegmentDurationSeconds cannot be negative.");
        Require(this.LeaderPersistLoops > 0, "leaderPersistLoops must be greater than zero.");
        Require(this.MaxRecognitionAttempts > 0, "maxRecognitionAttempts must be greater than zero.");

        Require(
            this.BufferSeconds >= this.WindowSeconds,
            $"bufferSeconds ({this.BufferSeconds}) must be at least windowSeconds ({this.WindowSeconds}).");

        Require(
            this.NormalizePeakTarget is > 0f and <= 1f,
            "normalizePeakTarget must be greater than 0 and at most 1.");

        Require(
            this.MinLeaderShare is > 0.0 and <= 1.0,
            "minLeaderShare must be greater than 0 and at most 1.");

        Require(
            this.VoteWeightsNewestToOldest.Length == this.RingBufferMaxSamples,
            $"voteWeightsNewestToOldest has {this.VoteWeightsNewestToOldest.Length} entries "
            + $"but ringBufferMaxSamples is {this.RingBufferMaxSamples}; they must match.");

        Require(
            this.VoteWeightsNewestToOldest.All(weight => weight >= 0.0 && !double.IsNaN(weight)),
            "voteWeightsNewestToOldest must all be non-negative numbers.");

        Require(
            this.VoteWeightsNewestToOldest.Any(weight => weight > 0.0),
            "voteWeightsNewestToOldest must contain at least one positive weight.");

        Require(
            this.SilenceExitDbfsThreshold >= this.SilenceEnterDbfsThreshold,
            $"silenceExitDbfsThreshold ({this.SilenceExitDbfsThreshold}) must be at or above "
            + $"silenceEnterDbfsThreshold ({this.SilenceEnterDbfsThreshold}) for hysteresis to work.");

        Require(
            this.RingBufferMaxAgeSeconds >= this.RingBufferMaxSamples * this.IntervalSeconds,
            $"ringBufferMaxAgeSeconds ({this.RingBufferMaxAgeSeconds}) is shorter than the "
            + $"{this.RingBufferMaxSamples} samples it should hold at {this.IntervalSeconds}s apart, "
            + "so votes would expire unpredictably.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
