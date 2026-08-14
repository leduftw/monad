namespace Monad.Recognition;

/// <summary>
/// The ISRC codes a recognition response carried, and the one Monad chose.
/// </summary>
/// <param name="Selected">The preferred code, or <c>null</c> when none was supplied.</param>
/// <param name="TopLevel">The response's own <c>isrc</c> field.</param>
/// <param name="Spotify">The code Spotify reported for the same track.</param>
/// <param name="Apple">The code Apple Music reported for the same track.</param>
/// <param name="Suffix">Display text summarising the above.</param>
public sealed record class IsrcInfo(
    string? Selected,
    string? TopLevel,
    string? Spotify,
    string? Apple,
    string Suffix)
{
    /// <summary>True when the sources disagreed about which recording this is.</summary>
    public bool HasMismatch { get; init; }

    public static IsrcInfo None { get; } = new(null, null, null, null, "[ISRC: n/a]");
}
