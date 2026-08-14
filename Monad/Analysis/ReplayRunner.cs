using System;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;
using Monad.Audio;
using Monad.Output;
using Monad.Recognition;

namespace Monad.Analysis;

/// <summary>
/// Runs a WAV file through the same analysis the live monitor uses, on a
/// synthetic clock. Useful for tuning thresholds against a known recording, and
/// -- with <c>--no-recognize</c> -- for exercising the whole pipeline without
/// spending API quota.
/// </summary>
public static class ReplayRunner
{
    public static async Task<RunSummary> RunAsync(
        DecodedAudio audio,
        ISongRecognizer recognizer,
        ISegmentSink sink,
        MonadConfig config,
        DateTimeOffset startUtc,
        MonadLog log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        config.Validate();

        log.Status($"Replaying {audio.Duration.TotalSeconds:F1}s of audio at {audio.SampleRate} Hz.");
        log.Status($"Analysing {config.WindowSeconds}s windows every {config.IntervalSeconds}s.");

        SegmentTracker tracker = new(config);
        WindowAnalyzer analyzer = new(recognizer, config, log);

        int windowSamples = config.WindowSeconds * audio.SampleRate;
        int stepSamples = config.IntervalSeconds * audio.SampleRate;
        float[] window = new float[windowSamples];

        int windows = 0;
        int lookups = 0;
        int segments = 0;
        DateTimeOffset endUtc = startUtc;

        for (int end = windowSamples; end <= audio.Mono.Length; end += stepSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            audio.Mono.AsSpan(end - windowSamples, windowSamples).CopyTo(window);
            endUtc = startUtc + TimeSpan.FromSeconds((double)end / audio.SampleRate);
            windows++;

            AnalysisStep step = await analyzer
                .AnalyzeAsync(window, audio.SampleRate, endUtc, cancellationToken)
                .ConfigureAwait(false);

            if (step.PerformedLookup)
            {
                lookups++;
            }

            if (step.Sample is null)
            {
                continue;
            }

            if (tracker.OnSample(step.Sample) is { } finalized)
            {
                segments++;
                await sink.EmitAsync(finalized, cancellationToken).ConfigureAwait(false);
            }
        }

        if (windows == 0)
        {
            log.Status(
                $"The file is shorter than one {config.WindowSeconds}s window, so nothing was analysed.");
        }

        if (tracker.Flush(endUtc) is { } last)
        {
            segments++;
            await sink.EmitAsync(last, cancellationToken).ConfigureAwait(false);
        }

        return new RunSummary(windows, lookups, segments);
    }
}
