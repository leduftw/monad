using System;
using System.Collections.Generic;
using System.Linq;

namespace Monad.Aggregation;

public sealed class WeightedLeaderElection
{
    private readonly int maxSamples;
    private readonly TimeSpan maxAge;
    private readonly double[] weightsNewestToOldest;

    private readonly LinkedList<RecognitionSample> samples; // oldest -> newest

    public WeightedLeaderElection(int maxSamples, TimeSpan maxAge, double[] weightsNewestToOldest)
    {
        this.maxSamples = maxSamples;
        this.maxAge = maxAge;
        this.weightsNewestToOldest = weightsNewestToOldest;
        this.samples = new LinkedList<RecognitionSample>();

        if (weightsNewestToOldest.Length != maxSamples)
        {
            throw new ArgumentException("VoteWeightsNewestToOldest length must equal RingBufferMaxSamples.");
        }
    }

    public void Add(RecognitionSample sample)
    {
        this.samples.AddLast(sample);

        this.Prune(sample.TimestampUtc);
    }

    private void Prune(DateTime nowUtc)
    {
        while (this.samples.First is not null)
        {
            TimeSpan age = nowUtc - this.samples.First.Value.TimestampUtc;
            if (age <= this.maxAge)
            {
                break;
            }

            this.samples.RemoveFirst();
        }

        while (this.samples.Count > this.maxSamples)
        {
            this.samples.RemoveFirst();
        }
    }

    public LeaderSnapshot ComputeLeader()
    {
        // Apply weights newest->oldest across current samples count
        // If we have fewer than maxSamples, use the newest subset of weights.
        int count = this.samples.Count;
        if (count == 0)
        {
            return LeaderSnapshot.Empty;
        }

        List<RecognitionSample> list = [.. this.samples]; // oldest -> newest

        Dictionary<string, double> votes = new(StringComparer.OrdinalIgnoreCase);
        double totalWeight = 0.0;

        // iterate oldest->newest, map to corresponding weight (newest=index 0, oldest=index count-1)
        for (int i = 0; i < count; i++)
        {
            RecognitionSample s = list[i];
            double w = this.weightsNewestToOldest[count - 1 - i];
            totalWeight += w;

            if (s.SongKey is null)
            {
                continue;
            }

            if (!votes.TryGetValue(s.SongKey, out double existing))
            {
                existing = 0.0;
            }

            votes[s.SongKey] = existing + w;
        }

        if (votes.Count == 0 || totalWeight <= 1e-9)
        {
            return LeaderSnapshot.Empty;
        }

        KeyValuePair<string, double> best = votes.OrderByDescending(kvp => kvp.Value).First();
        double share = best.Value / totalWeight;

        return new LeaderSnapshot(best.Key, share);
    }

    public ScatterStats ComputeScatterStats()
    {
        // Used for UNKNOWN: "mostly no match or scattered"
        // We treat "scattered" as: many distinct keys, no dominant, many no-match.
        int count = this.samples.Count;
        if (count == 0)
        {
            return ScatterStats.Empty;
        }

        int noMatchCount = 0;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecognitionSample sample in this.samples)
        {
            if (sample.IsNoMatch || sample.SongKey is null)
            {
                noMatchCount++;
            }
            else
            {
                keys.Add(sample.SongKey);
            }
        }

        return new ScatterStats(count, noMatchCount, keys.Count);
    }
}
