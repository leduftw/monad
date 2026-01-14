namespace Monad;

public sealed record class MonadConfig
{
    // Sampling
    public int RecordSeconds { get; init; } = 12;
    public int SleepBetweenSeconds { get; init; } = 4;

    // Ring buffer window
    public int RingBufferMaxSamples { get; init; } = 5;
    public int RingBufferMaxAgeSeconds { get; init; } = 90;

    // Vote weights (newest -> oldest), must match RingBufferMaxSamples length
    public double[] VoteWeightsNewestToOldest { get; init; } = [1.0, 0.8, 0.6, 0.4, 0.2];

    // Leader election + hysteresis
    public double MinLeaderShare { get; init; } = 0.60;
    public int LeaderPersistLoops { get; init; } = 2;

    // Segment logging
    public int MinSegmentDurationSeconds { get; init; } = 30;

    // Silence detection
    public double SilenceEnterDbfsThreshold { get; init; } = -65.0;
    public int SilenceEnterPersistLoops { get; init; } = 2;

    public double SilenceExitDbfsThreshold { get; init; } = -60.0;
    public int SilenceExitPersistLoops { get; init; } = 1;

    // Unknown detection
    public double UnknownMinDbfs { get; init; } = -60.0;
    public int UnknownPersistLoops { get; init; } = 2;
}
