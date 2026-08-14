using System.Text.Json;

namespace Monad.Recognition;

public sealed record class RecognitionResult(string Artist, string Title, IsrcInfo IsrcInfo)
{
    /// <summary>The one-line form written to the console and the segment log.</summary>
    public string DisplayText
    {
        get
        {
            string name = (this.Artist.Length, this.Title.Length) switch
            {
                ( > 0, > 0) => $"{this.Artist} - {this.Title}",
                ( > 0, 0) => this.Artist,
                (0, > 0) => this.Title,
                _ => "Unidentified track",
            };

            return $"{name} {this.IsrcInfo.Suffix}";
        }
    }

    public static RecognitionResult FromAuddResult(JsonElement result) =>
        new(
            Artist: ReadString(result, "artist"),
            Title: ReadString(result, "title"),
            IsrcInfo: IsrcExtractor.Extract(result));

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? string.Empty).Trim()
                : string.Empty;
}
