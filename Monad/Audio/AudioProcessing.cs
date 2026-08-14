using System;

namespace Monad.Audio;

public static class AudioProcessing
{
    /// <summary>
    /// Level reported for digitally silent audio. Well below the noise floor of
    /// any real recording, so it compares correctly against every threshold,
    /// while staying a finite number that survives JSON serialization.
    /// </summary>
    public const double SilenceFloorDbfs = -120.0;

    /// <summary>
    /// Averages interleaved channels into <paramref name="mono"/> and returns
    /// the number of frames written. Trailing samples of an incomplete final
    /// frame are ignored.
    /// </summary>
    public static int DownmixToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        int frames = interleaved.Length / channels;

        if (frames > mono.Length)
        {
            throw new ArgumentException(
                $"Destination holds {mono.Length} samples but {frames} frames were supplied.", nameof(mono));
        }

        if (channels == 1)
        {
            interleaved[..frames].CopyTo(mono);
            return frames;
        }

        float scale = 1f / channels;

        for (int frame = 0; frame < frames; frame++)
        {
            ReadOnlySpan<float> slice = interleaved.Slice(frame * channels, channels);

            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                sum += slice[channel];
            }

            mono[frame] = sum * scale;
        }

        return frames;
    }

    /// <summary>Root-mean-square level in dBFS, where 0 dBFS is full scale.</summary>
    public static double RmsDbfs(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return SilenceFloorDbfs;
        }

        double sumOfSquares = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            double sample = samples[i];
            sumOfSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumOfSquares / samples.Length);

        return rms <= 1e-12 ? SilenceFloorDbfs : Math.Max(SilenceFloorDbfs, 20.0 * Math.Log10(rms));
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;

        for (int i = 0; i < samples.Length; i++)
        {
            float magnitude = Math.Abs(samples[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    /// <summary>
    /// Scales <paramref name="samples"/> in place so its loudest sample sits at
    /// <paramref name="peakTarget"/>. Quiet audio is boosted; audio that is
    /// already louder is left alone, so this can never introduce clipping.
    /// </summary>
    /// <remarks>
    /// This exists to give the recognition API a healthy signal level. Measure
    /// loudness <em>before</em> calling it -- normalizing first would push every
    /// window towards the target and make silence look loud.
    /// </remarks>
    public static void BoostToPeak(Span<float> samples, float peakTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakTarget);

        float peak = Peak(samples);

        if (peak <= 0f)
        {
            return;
        }

        float scale = peakTarget / peak;

        if (scale <= 1.0f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * scale, -1f, 1f);
        }
    }
}
