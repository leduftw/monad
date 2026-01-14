using System;
using System.Text.Json;

namespace Monad.Recognition;

public static class IsrcExtractor
{
    public static IsrcInfo Extract(JsonElement result)
    {
        string? isrcTop = result.TryGetProperty("isrc", out JsonElement isrcElem) ? isrcElem.GetString() : null;

        string? isrcSpotify = TryGetSpotifyIsrc(result);
        string? isrcApple = TryGetAppleIsrc(result);

        string? selected = null;

        if (!string.IsNullOrWhiteSpace(isrcTop))
        {
            selected = isrcTop;
        }
        else if (!string.IsNullOrWhiteSpace(isrcSpotify))
        {
            selected = isrcSpotify;
        }
        else if (!string.IsNullOrWhiteSpace(isrcApple))
        {
            selected = isrcApple;
        }

        string suffix;

        if (string.IsNullOrWhiteSpace(selected))
        {
            suffix = "[ISRC: n/a]";
        }
        else
        {
            bool spotifyHas = !string.IsNullOrWhiteSpace(isrcSpotify);
            bool appleHas = !string.IsNullOrWhiteSpace(isrcApple);

            suffix = spotifyHas &&
                appleHas &&
                !string.Equals(isrcSpotify, isrcApple, StringComparison.OrdinalIgnoreCase)
                ? $"[ISRC mismatch: spotify={isrcSpotify}, apple={isrcApple}]"
                : $"[ISRC: {selected}]";
        }

        return new IsrcInfo(selected, isrcSpotify, isrcApple, suffix);
    }

    private static string? TryGetSpotifyIsrc(JsonElement result)
    {
        return !result.TryGetProperty("spotify", out JsonElement spotify) ||
            spotify.ValueKind != JsonValueKind.Object ||
            !spotify.TryGetProperty("external_ids", out JsonElement externalIds) ||
            externalIds.ValueKind != JsonValueKind.Object ||
            !externalIds.TryGetProperty("isrc", out JsonElement spotifyIsrcElem) ||
            spotifyIsrcElem.ValueKind != JsonValueKind.String
            ? null
            : spotifyIsrcElem.GetString();
    }

    private static string? TryGetAppleIsrc(JsonElement result)
    {
        return !result.TryGetProperty("apple_music", out JsonElement apple) ||
            apple.ValueKind != JsonValueKind.Object ||
            !apple.TryGetProperty("isrc", out JsonElement appleIsrcElem) ||
            appleIsrcElem.ValueKind != JsonValueKind.String
            ? null
            : appleIsrcElem.GetString();
    }
}
