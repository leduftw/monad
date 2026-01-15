using System;

namespace Monad.Aggregation;

public enum SegmentKind
{
    Song = 0,
    Silence = 1,
    Unknown = 2,
}

public sealed record class Segment(
    SegmentKind Kind,
    DateTime StartUtc,
    DateTime EndUtc,
    string? SongKey,
    string? DisplayText);
