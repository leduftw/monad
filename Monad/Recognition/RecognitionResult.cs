using System.Text.Json;

namespace Monad.Recognition;

public sealed record class RecognitionResult(string Artist, string Title, IsrcInfo IsrcInfo)
{
    public static RecognitionResult FromAuddResult(JsonElement result)
    {
        string artist = result.TryGetProperty("artist", out JsonElement a) && a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "").Trim() : "";
        string title = result.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";

        IsrcInfo isrcInfo = IsrcExtractor.Extract(result);

        return new RecognitionResult(
            Artist: artist,
            Title: title,
            IsrcInfo: isrcInfo);
    }
}
