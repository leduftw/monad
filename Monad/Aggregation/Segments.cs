using System;

namespace Monad.Aggregation;

public enum SegmentKind
{
    /// <summary>A recognised track held steady for long enough to report.</summary>
    Song = 0,

    /// <summary>Nothing was playing.</summary>
    Silence = 1,

    /// <summary>Audio was playing but could not be pinned to one track -- ads, speech, talk over music.</summary>
    Unknown = 2,
}

public sealed record class Segment(
    SegmentKind Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? SongKey,
    string? DisplayText)
{
    public TimeSpan Duration => this.EndUtc - this.StartUtc;

    /// <summary>What to show for this segment: the track, or the state's name.</summary>
    public string Label => this.Kind == SegmentKind.Song && !string.IsNullOrWhiteSpace(this.DisplayText)
        ? this.DisplayText
        : this.Kind.ToString().ToUpperInvariant();
}

/// <summary>One analysed window: how loud it was, and what was recognised in it.</summary>
/// <param name="TimestampUtc">The end of the analysed window -- what was playing as of this instant.</param>
/// <param name="IsNoMatch">True when nothing was identified, whether the lookup ran or was skipped.</param>
/// <param name="DisplayText">
/// How to render <paramref name="SongKey"/>. Carried on the sample because the
/// vote can elect a track whose name was only ever seen on an earlier window.
/// </param>
public sealed record class RecognitionSample(
    DateTimeOffset TimestampUtc,
    double Dbfs,
    string? SongKey,
    bool IsNoMatch,
    string? DisplayText = null);
