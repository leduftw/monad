using System;

namespace Monad.Aggregation;

public sealed class SegmentTracker
{
    private readonly MonadConfig config;
    private readonly WeightedLeaderElection election;

    private SegmentState? active;

    private int silenceEnterCount;
    private int silenceExitCount;

    private int unknownCount;

    private string? pendingLeaderKey;
    private int pendingLeaderCount;

    public SegmentTracker(MonadConfig config)
    {
        this.config = config;
        this.election = new WeightedLeaderElection(
            maxSamples: config.RingBufferMaxSamples,
            maxAge: TimeSpan.FromSeconds(config.RingBufferMaxAgeSeconds),
            weightsNewestToOldest: config.VoteWeightsNewestToOldest);
    }

    // Returns a segment when one is finalized AND passes MinSegmentDurationSeconds.
    // Otherwise returns null (segment dropped or not ended yet).
    public Segment? OnSample(RecognitionSample sample, string? displayTextIfSong)
    {
        this.election.Add(sample);

        // 1) SILENCE gate
        bool isSilent = sample.Dbfs <= this.config.SilenceEnterDbfsThreshold;

        if (this.IsInSilence())
        {
            bool exitSilence = sample.Dbfs > this.config.SilenceExitDbfsThreshold;
            if (exitSilence)
            {
                this.silenceExitCount++;
            }
            else
            {
                this.silenceExitCount = 0;
            }

            if (this.silenceExitCount >= this.config.SilenceExitPersistLoops)
            {
                // leave silence; reset counters and proceed to leader/unknown evaluation
                this.silenceEnterCount = 0;
                this.silenceExitCount = 0;

                // Note: do not immediately start a new segment here; fallthrough below.
            }
            else
            {
                // Stay in silence; nothing to change.
                return null;
            }
        }

        if (isSilent)
        {
            this.silenceEnterCount++;
        }
        else
        {
            this.silenceEnterCount = 0;
        }

        if (this.silenceEnterCount >= this.config.SilenceEnterPersistLoops)
        {
            // Enter/keep SILENCE. Start time is acceptance time (now).
            return this.SwitchTo(
                kind: SegmentKind.Silence,
                nowUtc: sample.TimestampUtc,
                songKey: null,
                displayText: null);
        }

        // 2) Leader election
        LeaderSnapshot snapshot = this.election.ComputeLeader();

        bool hasEligibleLeader = snapshot.LeaderKey is not null && snapshot.LeaderShare >= this.config.MinLeaderShare;

        string? acceptedLeader = null;

        if (hasEligibleLeader)
        {
            if (string.Equals(snapshot.LeaderKey, this.pendingLeaderKey, StringComparison.OrdinalIgnoreCase))
            {
                this.pendingLeaderCount++;
            }
            else
            {
                this.pendingLeaderKey = snapshot.LeaderKey;
                this.pendingLeaderCount = 1;
            }

            if (this.pendingLeaderCount >= this.config.LeaderPersistLoops)
            {
                acceptedLeader = snapshot.LeaderKey;
            }
        }
        else
        {
            this.pendingLeaderKey = null;
            this.pendingLeaderCount = 0;
        }

        if (acceptedLeader is not null)
        {
            this.unknownCount = 0;
            return this.SwitchTo(
                kind: SegmentKind.Song,
                nowUtc: sample.TimestampUtc,
                songKey: acceptedLeader,
                displayText: displayTextIfSong);
        }

        // 3) UNKNOWN (audio present, no stable leader)
        bool unknownEligibleByDbfs = sample.Dbfs > this.config.UnknownMinDbfs;

        bool isMostlyNoMatchOrScattered = this.IsMostlyNoMatchOrScattered();

        if (unknownEligibleByDbfs && isMostlyNoMatchOrScattered)
        {
            this.unknownCount++;
        }
        else
        {
            this.unknownCount = 0;
        }

        return this.unknownCount >= this.config.UnknownPersistLoops
            ? this.SwitchTo(
                kind: SegmentKind.Unknown,
                nowUtc: sample.TimestampUtc,
                songKey: null,
                displayText: null)
            : null;
    }

    private bool IsMostlyNoMatchOrScattered()
    {
        ScatterStats stats = this.election.ComputeScatterStats();
        if (stats.TotalSamples <= 0)
        {
            return true;
        }

        // Concrete definition:
        // - "mostly no match" => >= 60% no-match
        // - OR "scattered" => >= 2 distinct keys AND no stable leader (handled outside)
        double noMatchShare = (double)stats.NoMatchSamples / stats.TotalSamples;

        return noMatchShare >= 0.60 || stats.DistinctSongKeys >= 2;
    }

    private bool IsInSilence() => this.active is not null && this.active.Kind == SegmentKind.Silence;

    private Segment? SwitchTo(SegmentKind kind, DateTime nowUtc, string? songKey, string? displayText)
    {
        // If already in the same segment type (and same song key for songs), do nothing.
        if (this.active is not null)
        {
            if (this.active.Kind == kind)
            {
                if (kind != SegmentKind.Song)
                {
                    return null;
                }

                if (string.Equals(this.active.SongKey, songKey, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        // Close existing segment at nowUtc
        Segment? finalized = null;

        if (this.active is not null)
        {
            finalized = this.active.Finalize(nowUtc, this.config.MinSegmentDurationSeconds);
        }

        // Start new segment at acceptance time
        this.active = new SegmentState(kind, nowUtc, songKey, displayText);

        return finalized;
    }

    private sealed class SegmentState
    {
        public SegmentKind Kind
        {
            get;
        }
        public DateTime StartUtc
        {
            get;
        }
        public string? SongKey
        {
            get;
        }
        public string? DisplayText
        {
            get;
        }

        public SegmentState(SegmentKind kind, DateTime startUtc, string? songKey, string? displayText)
        {
            this.Kind = kind;
            this.StartUtc = startUtc;
            this.SongKey = songKey;
            this.DisplayText = displayText;
        }

        public Segment? Finalize(DateTime endUtc, int minSeconds)
        {
            TimeSpan dur = endUtc - this.StartUtc;

            return dur.TotalSeconds < minSeconds
                ? null
                : new Segment(
                Kind: this.Kind,
                StartUtc: this.StartUtc,
                EndUtc: endUtc,
                SongKey: this.SongKey,
                DisplayText: this.DisplayText);
        }
    }
}
