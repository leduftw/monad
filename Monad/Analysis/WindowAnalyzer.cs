using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;
using Monad.Audio;
using Monad.Recognition;

namespace Monad.Analysis;

/// <param name="Sample">
/// The vote this window contributes, or <c>null</c> when the lookup failed. A
/// failure is not evidence of anything -- feeding it in as "nothing playing"
/// would let an API outage end the song that is still playing.
/// </param>
public sealed record class AnalysisStep(RecognitionSample? Sample, double Dbfs, bool PerformedLookup);

/// <summary>
/// Measures one window of audio and, if there is anything to hear, identifies
/// it. Shared by live monitoring and <c>monad replay</c> so both behave the
/// same way.
/// </summary>
public sealed class WindowAnalyzer(ISongRecognizer recognizer, MonadConfig config, MonadLog log)
{
    private float[] payload = [];
    private int consecutiveFatalFailures;

    public async Task<AnalysisStep> AnalyzeAsync(
        ReadOnlyMemory<float> window,
        int sampleRate,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        // Measured on the untouched window. Normalising first -- as this once
        // did -- lifts every quiet passage towards full scale, which makes
        // silence read as loud audio and stops silence ever being detected.
        double dbfs = AudioProcessing.RmsDbfs(window.Span);

        log.Status($"[{endUtc.ToLocalTime():HH:mm:ss}]  {FormatLevel(dbfs)}");

        if (dbfs <= config.SilenceEnterDbfsThreshold)
        {
            // Nothing worth identifying, so no lookup is spent on it.
            return new AnalysisStep(
                new RecognitionSample(endUtc, dbfs, SongKey: null, IsNoMatch: true),
                dbfs,
                PerformedLookup: false);
        }

        if (this.payload.Length != window.Length)
        {
            this.payload = new float[window.Length];
        }

        window.Span.CopyTo(this.payload);
        AudioProcessing.BoostToPeak(this.payload, config.NormalizePeakTarget);

        byte[] wav = WavEncoder.ToWavPcm16Mono(this.payload, sampleRate);

        RecognitionOutcome outcome = await recognizer.RecognizeAsync(wav, cancellationToken).ConfigureAwait(false);

        return new AnalysisStep(this.Interpret(outcome, endUtc, dbfs), dbfs, PerformedLookup: true);
    }

    private RecognitionSample? Interpret(RecognitionOutcome outcome, DateTimeOffset endUtc, double dbfs)
    {
        switch (outcome.Status)
        {
            case RecognitionStatus.Match:
                this.consecutiveFatalFailures = 0;

                RecognitionResult result = outcome.Result!;
                string? key = SongKey.FromResult(result);

                log.Status($"           {result.DisplayText}");

                if (result.IsrcInfo.HasMismatch)
                {
                    log.Diagnostic($"ISRC sources disagree: {result.IsrcInfo.Suffix}");
                }

                return new RecognitionSample(endUtc, dbfs, key, IsNoMatch: key is null, result.DisplayText);

            case RecognitionStatus.NoMatch:
                this.consecutiveFatalFailures = 0;
                log.Diagnostic("no match");

                return new RecognitionSample(endUtc, dbfs, SongKey: null, IsNoMatch: true);

            default:
                log.Status($"           ! {outcome.ErrorMessage}");

                if (outcome.IsTransient)
                {
                    this.consecutiveFatalFailures = 0;
                }
                else if (++this.consecutiveFatalFailures >= config.MaxConsecutiveFatalFailures)
                {
                    throw new MonadFatalException(
                        $"Giving up after {this.consecutiveFatalFailures} unrecoverable recognition failures. "
                        + $"Last error: {outcome.ErrorMessage}");
                }

                return null;
        }
    }

    private static string FormatLevel(double dbfs) =>
        dbfs <= AudioProcessing.SilenceFloorDbfs
            ? "  silent"
            : string.Create(CultureInfo.InvariantCulture, $"{dbfs,6:F1} dBFS");
}
