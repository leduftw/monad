using System.Net.Http.Headers;
using System.Text.Json;

using NAudio.Wave;

const int RecordSeconds = 12;
const int SleepBetweenSeconds = 4;

const string AuddUrl = "https://api.audd.io/";

string? token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("AUDD_API_TOKEN not set. Example: setx AUDD_API_TOKEN \"<token>\"");
    return;
}

using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

string? lastPrint = null;

Console.WriteLine("Monitoring (AudD)... Ctrl+C to stop.");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    try
    {
        Console.WriteLine($"\nRecording {RecordSeconds}s...");

        // 1) Record loopback (PCM float) -> convert to int16 interleaved
        (short[] pcm16Interleaved, int sampleRate, int channels) =
            await RecordLoopbackAsync(TimeSpan.FromSeconds(RecordSeconds), cts.Token);

        // 2) Downmix to mono int16
        short[] mono = DownmixToMono(pcm16Interleaved, channels);

        // 3) Normalize (boost only if peak below target)
        mono = Normalize(mono, peakTarget: 0.90f);

        // 4) Print level
        double dbfs = RmsDbfs(mono);
        Console.WriteLine($"Level: {dbfs:F1} dBFS (0 dBFS = max)");

        // 5) Encode WAV in-memory
        byte[] wavBytes = ToWavPcm16(mono, sampleRate);

        Console.WriteLine("Recognizing...");
        using JsonDocument resp = await AuddRecognizeAsync(http, wavBytes, token, cts.Token);

        string? status = resp.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;

        if (status != "success" ||
            !resp.RootElement.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind == JsonValueKind.Null)
        {
            Console.WriteLine($"No match. (status={status})");
        }
        else
        {
            string artist = result.TryGetProperty("artist", out JsonElement a) ? (a.GetString() ?? "").Trim() : "";
            string title = result.TryGetProperty("title", out JsonElement t) ? (t.GetString() ?? "").Trim() : "";

            string line = $"{artist} - {title}".Trim(' ', '-');

            if (!string.Equals(line, lastPrint, StringComparison.Ordinal))
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Console.WriteLine($"{ts}  {line}");
                lastPrint = line;
            }
            else
            {
                Console.WriteLine($"... same: {line}");
            }
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("\nStopping...");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(SleepBetweenSeconds), cts.Token);
    }
    catch (OperationCanceledException) { }
}

// ---------------------- helpers ----------------------

static async Task<(short[] pcm16Interleaved, int sampleRate, int channels)> RecordLoopbackAsync(
    TimeSpan duration,
    CancellationToken ct)
{
    // WASAPI loopback gets what the machine is playing
    using WasapiLoopbackCapture capture = new();

    int sampleRate = capture.WaveFormat.SampleRate;
    int channels = capture.WaveFormat.Channels;

    // WasapiLoopbackCapture produces IEEE float (typically 32-bit float).
    // We'll accumulate bytes, then convert float->int16.
    using MemoryStream ms = new();

    capture.DataAvailable += (_, e) =>
        // e.Buffer contains raw capture bytes
        ms.Write(e.Buffer, 0, e.BytesRecorded);

    TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    capture.RecordingStopped += (_, e) =>
    {
        if (e.Exception is not null)
        {
            tcs.TrySetException(e.Exception);
        }
        else
        {
            tcs.TrySetResult();
        }
    };

    capture.StartRecording();

    try
    {
        await Task.Delay(duration, ct);
    }
    finally
    {
        capture.StopRecording();
    }

    await tcs.Task;

    byte[] bytes = ms.ToArray();

    int floatCount = bytes.Length / 4;
    short[] pcm16 = new short[floatCount];

    for (int i = 0; i < floatCount; i++)
    {
        float f = BitConverter.ToSingle(bytes, i * 4);
        f = Math.Clamp(f, -1f, 1f);
        pcm16[i] = (short)Math.Round(f * 32767f);
    }

    return (pcm16, sampleRate, channels);
}

static short[] DownmixToMono(short[] interleaved, int channels)
{
    if (channels <= 1)
    {
        return interleaved;
    }

    int frames = interleaved.Length / channels;
    short[] mono = new short[frames];

    for (int i = 0; i < frames; i++)
    {
        int sum = 0;
        int baseIdx = i * channels;
        for (int c = 0; c < channels; c++)
        {
            sum += interleaved[baseIdx + c];
        }

        int avg = sum / channels;
        mono[i] = (short)Math.Clamp(avg, short.MinValue, short.MaxValue);
    }

    return mono;
}

static short[] Normalize(short[] mono, float peakTarget)
{
    if (mono.Length == 0)
    {
        return mono;
    }

    int peak = 0;
    foreach (short s in mono)
    {
        int abs = Math.Abs((int)s);
        if (abs > peak)
        {
            peak = abs;
        }
    }

    if (peak <= 0)
    {
        return mono;
    }

    float targetPeak = peakTarget * 32767f;
    float scale = targetPeak / peak;

    // Only boost the signal, we don't want to make it quieter
    if (scale <= 1.0f)
    {
        return mono;
    }

    short[] y = new short[mono.Length];
    for (int i = 0; i < mono.Length; i++)
    {
        float v = mono[i] * scale;
        v = Math.Clamp(v, -32768f, 32767f);
        y[i] = (short)Math.Round(v);
    }

    return y;
}

static double RmsDbfs(short[] mono)
{
    if (mono.Length == 0)
    {
        return -999.0;
    }

    double sumSq = 0;
    for (int i = 0; i < mono.Length; i++)
    {
        double x = mono[i] / 32768.0;
        sumSq += x * x;
    }

    double rms = Math.Sqrt(sumSq / mono.Length);

    return rms <= 1e-12 ? -999.0 : 20.0 * Math.Log10(rms);
}

static byte[] ToWavPcm16(short[] mono, int sampleRate)
{
    using MemoryStream ms = new();

    // Write WAV header + PCM16 mono data
    using (WaveFileWriter writer = new(ms, new WaveFormat(sampleRate, 16, 1)))
    {
        byte[] buf = new byte[mono.Length * 2];
        Buffer.BlockCopy(mono, 0, buf, 0, buf.Length);
        writer.Write(buf, 0, buf.Length);
        writer.Flush();
    }

    return ms.ToArray();
}

static async Task<JsonDocument> AuddRecognizeAsync(HttpClient http, byte[] wavBytes, string token, CancellationToken ct)
{
    using MultipartFormDataContent content = new()
    {
        {
            new StringContent(token),
            "api_token"
        },
        {
            new StringContent("spotify,apple_music"),
            "return"
        }
    };

    ByteArrayContent fileContent = new(wavBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

    content.Add(fileContent, "file", "snippet.wav");

    using HttpResponseMessage resp = await http.PostAsync(AuddUrl, content, ct);
    resp.EnsureSuccessStatusCode();

    await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
}
