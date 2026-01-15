using System;

namespace Monad.Aggregation;

public sealed record class RecognitionSample(
    DateTime TimestampUtc,
    double Dbfs,
    string? SongKey,
    bool IsNoMatch);
