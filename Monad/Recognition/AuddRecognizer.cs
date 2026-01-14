using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Recognition;

public sealed class AuddRecognizer(HttpClient httpClient, string token) : IAuddRecognizer
{
    private const string AuddUrl = "https://api.audd.io/";

    private readonly HttpClient httpClient = httpClient;
    private readonly string token = token;

    public async Task<JsonDocument> RecognizeAsync(byte[] wavBytes, CancellationToken ct)
    {
        using MultipartFormDataContent content = new()
        {
            { new StringContent(this.token), "api_token" },
            { new StringContent("spotify,apple_music"), "return" }
        };

        ByteArrayContent fileContent = new(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "snippet.wav");

        using HttpResponseMessage resp = await this.httpClient.PostAsync(AuddUrl, content, ct);
        resp.EnsureSuccessStatusCode();

        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
