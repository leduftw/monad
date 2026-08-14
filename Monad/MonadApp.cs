using System;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;
using Monad.Analysis;
using Monad.Audio;
using Monad.Output;
using Monad.Recognition;

namespace Monad;

public sealed record class RunSummary(int WindowsAnalyzed, int Lookups, int SegmentsEmitted);

/// <summary>
/// The monitoring loop. Capture runs continuously in the background while a
/// timer slices off the most recent window, measures it, and -- only when there
/// is something to hear -- asks the recognition service what it is.
/// </summary>
/// <remarks>
/// Capture is continuous rather than record-then-sleep. Besides closing the gap
/// where audio used to go unheard, it keeps the macOS aggregate device up for
/// the whole session instead of tearing down and rebuilding the machine's audio
/// graph on every loop.
/// </remarks>
public sealed class MonadApp(
    ISystemAudioSource source,
    ISongRecognizer recognizer,
    ISegmentSink sink,
    MonadConfig config,
    TimeProvider timeProvider,
    MonadLog log)
{
    /// <summary>Windows analysed with nothing captured at all before hinting at a blocked capture path.</summary>
    private const int SilentStartWarningTicks = 3;

    /// <summary>
    /// Built in <see cref="RunAsync"/> rather than here, so an unusable
    /// configuration is reported as such instead of throwing out of a field
    /// initializer before validation ever runs.
    /// </summary>
    private volatile RollingAudioBuffer? buffer;

    public async Task<RunSummary> RunAsync(CancellationToken cancellationToken)
    {
        config.Validate();

        this.buffer = new RollingAudioBuffer(
            config.PreferredSampleRate, TimeSpan.FromSeconds(config.BufferSeconds));

        log.Status($"Listening via {source.Description}.");
        log.Status($"Analysing the last {config.WindowSeconds}s every {config.IntervalSeconds}s. Ctrl+C to stop.");

        using CancellationTokenSource captureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task capture = Task.Run(() => this.PumpAsync(captureStop.Token), CancellationToken.None);

        try
        {
            return await this.AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await captureStop.CancelAsync().ConfigureAwait(false);

            try
            {
                await capture.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: we just asked it to stop.
            }
        }
    }

    /// <summary>Drains the capture source into the rolling buffer for as long as the run lasts.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AudioBlock block in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (block.Samples.Length == 0 || !block.Format.IsValid)
                {
                    continue;
                }

                RollingAudioBuffer current = this.buffer!;

                if (current.SampleRate != block.Format.SampleRate)
                {
                    log.Diagnostic($"capture format is {block.Format}");
                    current = new RollingAudioBuffer(
                        block.Format.SampleRate, TimeSpan.FromSeconds(config.BufferSeconds));
                    this.buffer = current;
                }

                current.AppendInterleaved(block.Samples.Span, block.Format.Channels, timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            log.Status($"Capture stopped: {ex.Message}");
        }
    }

    private async Task<RunSummary> AnalyzeAsync(CancellationToken cancellationToken)
    {
        SegmentTracker tracker = new(config);
        WindowAnalyzer analyzer = new(recognizer, config, log);

        float[] window = [];
        int windows = 0;
        int lookups = 0;
        int segments = 0;
        bool warnedAboutSilentStart = false;

        // Wait a full window before the first look, so the opening reading is
        // real audio rather than a buffer that has barely started filling.
        TimeSpan delay = config.Window;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = config.Interval;

            RollingAudioBuffer current = this.buffer!;
            int wanted = config.WindowSeconds * current.SampleRate;

            if (window.Length != wanted)
            {
                window = new float[wanted];
            }

            AudioWindow slice = current.CopyLatest(window, timeProvider.GetUtcNow());
            windows++;

            AnalysisStep step;

            try
            {
                step = await analyzer
                    .AnalyzeAsync(window, slice.SampleRate, slice.EndUtc, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (step.PerformedLookup)
            {
                lookups++;
            }

            if (!current.HasReceivedAudio && !warnedAboutSilentStart && windows >= SilentStartWarningTicks)
            {
                warnedAboutSilentStart = true;
                log.Status(SilentStartHint);
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

        // Without this the last thing heard in a session is never reported.
        if (tracker.Flush(timeProvider.GetUtcNow()) is { } last)
        {
            segments++;
            await sink.EmitAsync(last, CancellationToken.None).ConfigureAwait(false);
        }

        return new RunSummary(windows, lookups, segments);
    }

    private static string SilentStartHint =>
        OperatingSystem.IsMacOS()
            ? "No audio captured yet. If something is playing, grant this terminal permission under "
                + "System Settings > Privacy & Security > Screen & System Audio Recording."
            : "No audio captured yet. If something is playing, check that the capture device is available.";
}
