using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;
using Monad.Audio;
using Monad.Recognition;

namespace Monad;

public sealed class MonadApp(HttpClient httpClient, string auddToken, MonadConfig config)
{
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("Monitoring (AudD)... Ctrl+C to stop.");

        LoopbackRecorder recorder = new();
        AuddRecognizer recognizer = new(httpClient, auddToken);

        SegmentTracker tracker = new(config);

        while (!ct.IsCancellationRequested)
        {
            DateTime nowUtc = DateTime.UtcNow;

            try
            {
                Console.WriteLine($"\nRecording {config.RecordSeconds}s...");

                RecordedAudio audio = await recorder.RecordAsync(TimeSpan.FromSeconds(config.RecordSeconds), ct);

                short[] mono = AudioProcessing.DownmixToMono(audio.Pcm16Interleaved, audio.Channels);
                mono = AudioProcessing.NormalizeOnlyBoost(mono, peakTarget: 0.90f);

                double dbfs = AudioProcessing.RmsDbfs(mono);
                Console.WriteLine($"Level: {dbfs:F1} dBFS (0 dBFS = max)");

                byte[] wavBytes = WavEncoder.ToWavPcm16Mono(mono, audio.SampleRate);

                Console.WriteLine("Recognizing...");

                using JsonDocument resp = await recognizer.RecognizeAsync(wavBytes, ct);

                string? songKey = null;
                string? display = null;
                bool isNoMatch = true;

                if (TryParseRecognition(resp, out RecognitionResult? parsed))
                {
                    isNoMatch = false;
                    songKey = SongKey.FromResult(parsed!);
                    display = $"{parsed!.Artist} - {parsed!.Title} {parsed!.IsrcInfo.Suffix}".Trim(' ', '-');
                }

                RecognitionSample sample = new(
                    TimestampUtc: nowUtc,
                    Dbfs: dbfs,
                    SongKey: songKey,
                    IsNoMatch: isNoMatch);

                Segment? finalized = tracker.OnSample(sample, display);

                if (finalized is not null)
                {
                    PrintSegment(finalized);
                }
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
                await Task.Delay(TimeSpan.FromSeconds(config.SleepBetweenSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }
    }

    private static bool TryParseRecognition(JsonDocument resp, out RecognitionResult? parsed)
    {
        parsed = null;

        string? status = resp.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;

        if (status != "success" ||
            !resp.RootElement.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        parsed = RecognitionResult.FromAuddResult(result);
        return true;
    }

    private static void PrintSegment(Segment segment)
    {
        string start = segment.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        string end = segment.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        if (segment.Kind == SegmentKind.Song)
        {
            Console.WriteLine($"SEGMENT  [{start} - {end}]  {segment.DisplayText}");
        }
        else
        {
            Console.WriteLine($"SEGMENT  [{start} - {end}]  {segment.Kind.ToString().ToUpperInvariant()}");
        }
    }
}
