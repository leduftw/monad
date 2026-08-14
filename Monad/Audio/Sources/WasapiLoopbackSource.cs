using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using NAudio.Wave;

namespace Monad.Audio.Sources;

/// <summary>
/// Captures Windows system audio through WASAPI loopback. Runs in process, so
/// unlike the macOS and Linux paths there is no helper to spawn.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackSource(Action<string> logDiagnostic) : ISystemAudioSource
{
    private WasapiLoopbackCapture? capture;

    public string Description => "WASAPI loopback (Windows system audio)";

    public async IAsyncEnumerable<AudioBlock> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Bounded so a stalled consumer drops old audio instead of growing without limit.
        Channel<float[]> channel = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        WasapiLoopbackCapture capture = new();
        this.capture = capture;

        WaveFormat waveFormat = capture.WaveFormat;
        AudioFormat format = new(waveFormat.SampleRate, waveFormat.Channels);

        if (!IsSupported(waveFormat))
        {
            throw new NotSupportedException(
                $"The loopback device offered {waveFormat.Encoding} at {waveFormat.BitsPerSample}-bit; "
                + "Monad can read 32-bit float or 16-bit PCM.");
        }

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            float[]? samples = Convert(e.Buffer.AsSpan(0, e.BytesRecorded), waveFormat);

            if (samples is not null)
            {
                channel.Writer.TryWrite(samples);
            }
        };

        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                logDiagnostic($"WASAPI capture stopped: {e.Exception.Message}");
            }

            channel.Writer.TryComplete(e.Exception);
        };

        capture.StartRecording();

        try
        {
            await foreach (float[] samples in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new AudioBlock(samples, format);
            }
        }
        finally
        {
            capture.StopRecording();
        }
    }

    /// <summary>
    /// Loopback almost always hands back 32-bit float, but the device decides,
    /// so 16-bit PCM is handled too rather than being silently misread.
    /// </summary>
    private static bool IsSupported(WaveFormat format) =>
        (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        || (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16);

    private static float[]? Convert(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length - (bytes.Length % sizeof(float)))]).ToArray();
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            ReadOnlySpan<short> source = MemoryMarshal.Cast<byte, short>(bytes[..(bytes.Length - (bytes.Length % sizeof(short)))]);
            float[] samples = new float[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                samples[i] = source[i] / 32768f;
            }

            return samples;
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        WasapiLoopbackCapture? capture = this.capture;
        this.capture = null;

        try
        {
            capture?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // Already torn down by RecordingStopped.
        }

        return ValueTask.CompletedTask;
    }
}
