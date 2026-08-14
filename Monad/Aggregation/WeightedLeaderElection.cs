using System;
using System.Collections.Generic;

namespace Monad.Aggregation;

/// <param name="LeaderSinceUtc">
/// When the leading track was first heard in the current run. Recognition needs
/// several loops to settle, so this is meaningfully earlier than the moment the
/// leader is accepted -- using it as the segment boundary keeps reported start
/// times close to when the music actually started.
/// </param>
public readonly record struct LeaderSnapshot(string? LeaderKey, double LeaderShare, DateTimeOffset? LeaderSinceUtc)
{
    public static LeaderSnapshot Empty { get; } = new(null, 0.0, null);
}

public readonly record struct ScatterStats(int TotalSamples, int NoMatchSamples, int DistinctSongKeys)
{
    public static ScatterStats Empty { get; } = new(0, 0, 0);

    public double NoMatchShare => this.TotalSamples <= 0 ? 1.0 : (double)this.NoMatchSamples / this.TotalSamples;
}

/// <summary>
/// A short, recency-weighted vote over recent recognition results. A single
/// wrong answer cannot outvote several consistent ones, which is what keeps the
/// segment tracker from flapping between tracks.
/// </summary>
public sealed class WeightedLeaderElection
{
    private readonly int maxSamples;
    private readonly TimeSpan maxAge;
    private readonly double[] weightsNewestToOldest;
    private readonly List<RecognitionSample> samples = []; // oldest -> newest

    public WeightedLeaderElection(int maxSamples, TimeSpan maxAge, double[] weightsNewestToOldest)
    {
        ArgumentNullException.ThrowIfNull(weightsNewestToOldest);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);

        if (weightsNewestToOldest.Length != maxSamples)
        {
            throw new ArgumentException(
                $"Expected {maxSamples} vote weights to match the ring buffer size, "
                + $"but {weightsNewestToOldest.Length} were supplied.",
                nameof(weightsNewestToOldest));
        }

        foreach (double weight in weightsNewestToOldest)
        {
            if (weight < 0.0 || double.IsNaN(weight))
            {
                throw new ArgumentException("Vote weights must be non-negative numbers.", nameof(weightsNewestToOldest));
            }
        }

        this.maxSamples = maxSamples;
        this.maxAge = maxAge;
        this.weightsNewestToOldest = (double[])weightsNewestToOldest.Clone();
    }

    public int Count => this.samples.Count;

    public void Add(RecognitionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        this.samples.Add(sample);
        this.Prune(sample.TimestampUtc);
    }

    public void Clear() => this.samples.Clear();

    private void Prune(DateTimeOffset nowUtc)
    {
        int drop = 0;

        while (drop < this.samples.Count && nowUtc - this.samples[drop].TimestampUtc > this.maxAge)
        {
            drop++;
        }

        if (this.samples.Count - drop > this.maxSamples)
        {
            drop = this.samples.Count - this.maxSamples;
        }

        if (drop > 0)
        {
            this.samples.RemoveRange(0, drop);
        }
    }

    public LeaderSnapshot ComputeLeader()
    {
        int count = this.samples.Count;

        if (count == 0)
        {
            return LeaderSnapshot.Empty;
        }

        Dictionary<string, double> votes = new(StringComparer.OrdinalIgnoreCase);
        double totalWeight = 0.0;

        // The newest sample takes weights[0]; walking oldest -> newest means
        // index `count - 1 - i`. Partially filled buffers use the heaviest
        // weights, so early results are not artificially discounted.
        for (int i = 0; i < count; i++)
        {
            RecognitionSample sample = this.samples[i];
            double weight = this.weightsNewestToOldest[count - 1 - i];

            // No-match samples still count towards the denominator: a track has
            // to beat the silence and the noise, not just the other guesses.
            totalWeight += weight;

            if (sample.SongKey is null)
            {
                continue;
            }

            votes[sample.SongKey] = votes.GetValueOrDefault(sample.SongKey) + weight;
        }

        if (votes.Count == 0 || totalWeight <= 1e-9)
        {
            return LeaderSnapshot.Empty;
        }

        string? leader = null;
        double best = double.NegativeInfinity;

        foreach ((string key, double weight) in votes)
        {
            if (weight > best)
            {
                best = weight;
                leader = key;
            }
        }

        return new LeaderSnapshot(leader, best / totalWeight, this.FirstHeard(leader!));
    }

    /// <summary>
    /// Walks back from the newest sample to find when this run of the track
    /// started. Unrecognised samples do not end the run -- a couple of failed
    /// lookups in the middle of a song are normal -- but a different track does.
    /// </summary>
    private DateTimeOffset? FirstHeard(string key)
    {
        DateTimeOffset? since = null;

        for (int i = this.samples.Count - 1; i >= 0; i--)
        {
            RecognitionSample sample = this.samples[i];

            if (sample.SongKey is null)
            {
                continue;
            }

            if (!string.Equals(sample.SongKey, key, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            since = sample.TimestampUtc;
        }

        return since;
    }

    public ScatterStats ComputeScatterStats()
    {
        if (this.samples.Count == 0)
        {
            return ScatterStats.Empty;
        }

        int noMatch = 0;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecognitionSample sample in this.samples)
        {
            if (sample.IsNoMatch || sample.SongKey is null)
            {
                noMatch++;
            }
            else
            {
                keys.Add(sample.SongKey);
            }
        }

        return new ScatterStats(this.samples.Count, noMatch, keys.Count);
    }
}
