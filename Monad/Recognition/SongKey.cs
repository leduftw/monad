namespace Monad.Recognition;

public static class SongKey
{
    public static string? FromResult(RecognitionResult parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.IsrcInfo.Selected))
        {
            return "isrc:" + parsed.IsrcInfo.Selected.Trim().ToUpperInvariant();
        }

        string artist = (parsed.Artist ?? string.Empty).Trim();
        string title = (parsed.Title ?? string.Empty).Trim();

        if (artist.Length == 0 && title.Length == 0)
        {
            return null;
        }

        return "text:" + artist.ToUpperInvariant() + "|" + title.ToUpperInvariant();
    }
}
