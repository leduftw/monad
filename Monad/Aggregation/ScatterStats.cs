namespace Monad.Aggregation;

public readonly record struct ScatterStats(int TotalSamples, int NoMatchSamples, int DistinctSongKeys)
{
    public static ScatterStats Empty => new(0, 0, 0);
}
