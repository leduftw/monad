using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Monad.Audio;
using Monad.Recognition;

namespace Monad;

public sealed class MonadApp(HttpClient httpClient, string auddToken, int recordSeconds, int sleepBetweenSeconds)
{
    private string? lastPrint = null;

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("Monitoring (AudD)... Ctrl+C to stop.");

        LoopbackRecorder recorder = new();
        AuddRecognizer recognizer = new(httpClient, auddToken);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine($"\nRecording {recordSeconds}s...");

                RecordedAudio audio = await recorder.RecordAsync(TimeSpan.FromSeconds(recordSeconds), ct);

                short[] mono = AudioProcessing.DownmixToMono(audio.Pcm16Interleaved, audio.Channels);
                mono = AudioProcessing.NormalizeOnlyBoost(mono, peakTarget: 0.90f);

                double dbfs = AudioProcessing.RmsDbfs(mono);
                Console.WriteLine($"Level: {dbfs:F1} dBFS (0 dBFS = max)");

                byte[] wavBytes = WavEncoder.ToWavPcm16Mono(mono, audio.SampleRate);

                Console.WriteLine("Recognizing...");
                using JsonDocument resp = await recognizer.RecognizeAsync(wavBytes, ct);

                this.PrintRecognition(resp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                await Task.Delay(TimeSpan.FromSeconds(sleepBetweenSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }
    }

    private void PrintRecognition(JsonDocument resp)
    {
        string? status = resp.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;

        if (status != "success" ||
            !resp.RootElement.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind == JsonValueKind.Null)
        {
            Console.WriteLine($"No match. (status={status})");
            return;
        }

        RecognitionResult parsed = RecognitionResult.FromAuddResult(result);

        string line = $"{parsed.Artist} - {parsed.Title} {parsed.IsrcSuffix}".Trim(' ', '-');

        if (!string.Equals(line, this.lastPrint, StringComparison.Ordinal))
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"{ts}  {line}");
            this.lastPrint = line;
        }
        else
        {
            Console.WriteLine($"... same: {line}");
        }
    }
}
