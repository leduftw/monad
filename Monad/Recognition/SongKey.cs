namespace Monad.Recognition;

/// <summary>
/// Builds the identifier two recognitions are compared by.
/// </summary>
/// <remarks>
/// ISRC is preferred because it is stable: the same recording carries the same
/// code however its title is spelled, punctuated or transliterated. Text keys
/// are the fallback, and are matched case-insensitively for the same reason.
/// </remarks>
public static class SongKey
{
    public static string? FromResult(RecognitionResult parsed)
    {
        System.ArgumentNullException.ThrowIfNull(parsed);

        if (!string.IsNullOrWhiteSpace(parsed.IsrcInfo.Selected))
        {
            return "isrc:" + parsed.IsrcInfo.Selected.Trim().ToUpperInvariant();
        }

        string artist = parsed.Artist.Trim();
        string title = parsed.Title.Trim();

        return artist.Length == 0 && title.Length == 0
            ? null
            : $"text:{artist.ToUpperInvariant()}|{title.ToUpperInvariant()}";
    }
}
