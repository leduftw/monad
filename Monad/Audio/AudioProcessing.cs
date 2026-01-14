using System;

namespace Monad.Audio;

public static class AudioProcessing
{
    public static short[] DownmixToMono(short[] interleaved, int channels)
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

    public static short[] NormalizeOnlyBoost(short[] mono, float peakTarget)
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

    public static double RmsDbfs(short[] mono)
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
}
