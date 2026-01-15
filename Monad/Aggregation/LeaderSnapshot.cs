namespace Monad.Aggregation;

public readonly record struct LeaderSnapshot(string? LeaderKey, double LeaderShare)
{
    public static LeaderSnapshot Empty => new(null, 0.0);
}
