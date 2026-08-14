using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Recognition;

/// <summary>Recognises tracks through the AudD API (https://audd.io).</summary>
public sealed class AuddRecognizer(
    HttpClient httpClient,
    string token,
    int maxAttempts = 3,
    Action<string>? logDiagnostic = null) : ISongRecognizer
{
    private const string Endpoint = "https://api.audd.io/";

    /// <summary>AudD error codes that mean "your account cannot make this call".</summary>
    private static readonly int[] FatalErrorCodes = [900, 901];

    public async Task<RecognitionOutcome> RecognizeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);

        RecognitionOutcome outcome = RecognitionOutcome.Failed("No attempt was made.", isTransient: true);

        for (int attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            outcome = await this.AttemptAsync(wavBytes, cancellationToken).ConfigureAwait(false);

            if (outcome.Status != RecognitionStatus.Failed || !outcome.IsTransient)
            {
                return outcome;
            }

            if (attempt == maxAttempts)
            {
                break;
            }

            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            logDiagnostic?.Invoke($"{outcome.ErrorMessage} -- retrying in {backoff.TotalSeconds:F0}s");

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<RecognitionOutcome> AttemptAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        try
        {
            using MultipartFormDataContent content = new()
            {
                { new StringContent(token), "api_token" },
                { new StringContent("spotify,apple_music"), "return" },
            };

            ByteArrayContent file = new(wavBytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "snippet.wav");

            using HttpResponseMessage response = await httpClient
                .PostAsync(Endpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return RecognitionOutcome.Failed(
                    $"AudD returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    isTransient: IsTransient(response.StatusCode));
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Parse(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // Not our cancellation token, so this is HttpClient's own timeout.
            return RecognitionOutcome.Failed("AudD request timed out", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            return RecognitionOutcome.Failed($"AudD request failed: {ex.Message}", isTransient: true);
        }
        catch (JsonException ex)
        {
            return RecognitionOutcome.Failed($"AudD sent a malformed response: {ex.Message}", isTransient: true);
        }
    }

    /// <summary>
    /// Reads an AudD envelope: <c>{"status":"success","result":{...}}</c> for a
    /// hit, a null <c>result</c> for a miss, and <c>{"status":"error",...}</c>
    /// for anything else.
    /// </summary>
    public static RecognitionOutcome Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return RecognitionOutcome.Failed("AudD response was not a JSON object", isTransient: true);
        }

        string? status = root.TryGetProperty("status", out JsonElement statusElement)
            && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;

        if (!string.Equals(status, "success", StringComparison.Ordinal))
        {
            return ParseError(root);
        }

        if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object)
        {
            return RecognitionOutcome.NoMatch;
        }

        return RecognitionOutcome.Matched(RecognitionResult.FromAuddResult(result));
    }

    private static RecognitionOutcome ParseError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
        {
            return RecognitionOutcome.Failed("AudD reported an unspecified error", isTransient: true);
        }

        int code = error.TryGetProperty("error_code", out JsonElement codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : 0;

        string message = error.TryGetProperty("error_message", out JsonElement messageElement)
            && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? "unknown error"
                : "unknown error";

        return RecognitionOutcome.Failed(
            $"AudD error {code}: {message}",
            isTransient: Array.IndexOf(FatalErrorCodes, code) < 0);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
