using System;
using System.Collections.Generic;

namespace Monad.Aggregation;

/// <summary>
/// Turns a stream of per-window recognition results into a timeline of
/// segments. Three states -- <see cref="SegmentKind.Song"/>,
/// <see cref="SegmentKind.Silence"/> and <see cref="SegmentKind.Unknown"/> --
/// with hysteresis on every transition, so borderline audio does not make the
/// output flap.
/// </summary>
public sealed class SegmentTracker(MonadConfig config)
{
    private readonly WeightedLeaderElection election = new(
        maxSamples: config.RingBufferMaxSamples,
        maxAge: TimeSpan.FromSeconds(config.RingBufferMaxAgeSeconds),
        weightsNewestToOldest: config.VoteWeightsNewestToOldest);

    /// <summary>
    /// Last known display text per song key. The vote regularly elects a track
    /// from older samples while the current window recognised nothing, and
    /// without this the resulting segment would be reported with no name.
    /// </summary>
    private readonly Dictionary<string, string> displayTextByKey = new(StringComparer.OrdinalIgnoreCase);

    private ActiveSegment? active;

    private int silenceEnterCount;
    private int silenceExitCount;
    private int unknownCount;

    private string? pendingLeaderKey;
    private int pendingLeaderCount;

    /// <summary>The state currently being accumulated, or <c>null</c> before the first one starts.</summary>
    public SegmentKind? ActiveKind => this.active?.Kind;

    public string? ActiveSongKey => this.active?.SongKey;

    public DateTimeOffset? ActiveStartUtc => this.active?.StartUtc;

    /// <summary>
    /// Feeds in one analysed window. Returns the previous segment when this
    /// sample ended it and it was long enough to be worth reporting; otherwise
    /// <c>null</c>.
    /// </summary>
    public Segment? OnSample(RecognitionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        this.election.Add(sample);
        this.RememberDisplayText(sample);

        DateTimeOffset now = sample.TimestampUtc;

        if (this.active?.Kind == SegmentKind.Silence)
        {
            // Leaving silence takes sustained level above the (higher) exit
            // threshold; the gap between the two thresholds is the hysteresis.
            this.silenceExitCount = sample.Dbfs > config.SilenceExitDbfsThreshold ? this.silenceExitCount + 1 : 0;

            if (this.silenceExitCount < config.SilenceExitPersistLoops)
            {
                return null;
            }

            this.silenceExitCount = 0;
            this.silenceEnterCount = 0;

            // Fall through: something is playing again, work out what.
        }
        else
        {
            this.silenceEnterCount = sample.Dbfs <= config.SilenceEnterDbfsThreshold ? this.silenceEnterCount + 1 : 0;

            if (this.silenceEnterCount >= config.SilenceEnterPersistLoops)
            {
                this.ResetPendingLeader();
                this.unknownCount = 0;

                return this.SwitchTo(SegmentKind.Silence, now, now, songKey: null, displayText: null);
            }
        }

        LeaderSnapshot snapshot = this.election.ComputeLeader();

        if (snapshot.LeaderKey is not null && snapshot.LeaderShare >= config.MinLeaderShare)
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

            if (this.pendingLeaderCount >= config.LeaderPersistLoops)
            {
                this.unknownCount = 0;

                return this.SwitchTo(
                    SegmentKind.Song,
                    transitionUtc: now,
                    startUtc: snapshot.LeaderSinceUtc ?? now,
                    songKey: snapshot.LeaderKey,
                    displayText: this.displayTextByKey.GetValueOrDefault(snapshot.LeaderKey));
            }
        }
        else
        {
            this.ResetPendingLeader();
        }

        bool loudEnough = sample.Dbfs > config.UnknownMinDbfs;
        this.unknownCount = loudEnough && this.IsScattered() ? this.unknownCount + 1 : 0;

        return this.unknownCount >= config.UnknownPersistLoops
            ? this.SwitchTo(SegmentKind.Unknown, now, now, songKey: null, displayText: null)
            : null;
    }

    /// <summary>
    /// Closes the segment in progress, at shutdown. Without this the last thing
    /// heard in a session is never reported.
    /// </summary>
    public Segment? Flush(DateTimeOffset endUtc)
    {
        ActiveSegment? closing = this.active;
        this.active = null;

        return closing?.Finalize(endUtc, config.MinSegmentDurationSeconds);
    }

    /// <summary>
    /// Whether recent results are too inconsistent to name a track: mostly
    /// unrecognised, or bouncing between several different ones.
    /// </summary>
    private bool IsScattered()
    {
        ScatterStats stats = this.election.ComputeScatterStats();

        return stats.TotalSamples <= 0
            || stats.NoMatchShare >= config.UnknownNoMatchShare
            || stats.DistinctSongKeys >= config.UnknownMinDistinctKeys;
    }

    private void ResetPendingLeader()
    {
        this.pendingLeaderKey = null;
        this.pendingLeaderCount = 0;
    }

    private void RememberDisplayText(RecognitionSample sample)
    {
        if (sample.SongKey is null || string.IsNullOrWhiteSpace(sample.DisplayText))
        {
            return;
        }

        // A long session hears a lot of tracks; the vote can only ever elect one
        // that is still in the ring buffer, so an occasional reset costs nothing.
        if (this.displayTextByKey.Count >= 256)
        {
            this.displayTextByKey.Clear();
        }

        this.displayTextByKey[sample.SongKey] = sample.DisplayText;
    }

    /// <param name="transitionUtc">When this sample was analysed.</param>
    /// <param name="startUtc">
    /// When the new state is believed to have actually begun, which for a track
    /// is when it was first heard rather than when the vote settled.
    /// </param>
    private Segment? SwitchTo(
        SegmentKind kind,
        DateTimeOffset transitionUtc,
        DateTimeOffset startUtc,
        string? songKey,
        string? displayText)
    {
        if (this.active is { } current && current.Kind == kind)
        {
            bool sameSegment = kind != SegmentKind.Song
                || string.Equals(current.SongKey, songKey, StringComparison.OrdinalIgnoreCase);

            if (sameSegment)
            {
                return null;
            }
        }

        // One shared instant so the timeline has no gap or overlap across the
        // handover, clamped inside the window we actually know about.
        DateTimeOffset boundary = startUtc;

        if (this.active is { } previous && boundary < previous.StartUtc)
        {
            boundary = previous.StartUtc;
        }

        if (boundary > transitionUtc)
        {
            boundary = transitionUtc;
        }

        Segment? finalized = this.active?.Finalize(boundary, config.MinSegmentDurationSeconds);

        this.active = new ActiveSegment(kind, boundary, songKey, displayText);

        return finalized;
    }

    private sealed record class ActiveSegment(
        SegmentKind Kind,
        DateTimeOffset StartUtc,
        string? SongKey,
        string? DisplayText)
    {
        public Segment? Finalize(DateTimeOffset endUtc, int minimumSeconds)
        {
            TimeSpan duration = endUtc - this.StartUtc;

            // A segment that occupied no time says nothing, whatever the
            // configured minimum: a state entered on the very last window has
            // nothing to report.
            return duration <= TimeSpan.Zero || duration.TotalSeconds < minimumSeconds
                ? null
                : new Segment(this.Kind, this.StartUtc, endUtc, this.SongKey, this.DisplayText);
        }
    }
}
