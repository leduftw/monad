using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Monad.Audio.Sources;

/// <summary>
/// Captures Windows system audio through WASAPI loopback. Runs in process, so
/// unlike the macOS and Linux paths there is no helper to spawn.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackSource(Action<string> logDiagnostic) : ISystemAudioSource
{
    private WasapiRecorder? capture;

    public string Description => "WASAPI loopback (Windows system audio)";

    public async IAsyncEnumerable<AudioBlock> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Bounded so a stalled consumer drops old audio instead of growing without limit.
        // NAudio raises one event per WASAPI packet, usually 10 ms of audio, so this
        // holds about three seconds.
        Channel<float[]> channel = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(320) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        using MMDeviceEnumerator devices = new();
        using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        WasapiRecorder capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .Build();
        this.capture = capture;

        try
        {
            WaveFormat waveFormat = GetSupportedFormat(capture.WaveFormat);
            AudioFormat format = new(waveFormat.SampleRate, waveFormat.Channels);

            capture.DataAvailable += (bytes, flags, _, _) =>
            {
                if (!bytes.IsEmpty)
                {
                    // The recorder's span belongs to WASAPI and expires when this callback returns.
                    channel.Writer.TryWrite(Convert(bytes, waveFormat, flags));
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

            await foreach (float[] samples in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AudioBlock(samples, format);
            }
        }
        finally
        {
            await this.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loopback almost always hands back 32-bit float, but the device decides,
    /// so 16-bit PCM is handled too rather than being silently misread.
    /// </summary>
    internal static WaveFormat GetSupportedFormat(WaveFormat format)
    {
        // Unlike the legacy recorder, WasapiRecorder exposes the device's extensible mix format.
        format = format.AsStandardWaveFormat();

        if ((format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            || (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
        {
            return format;
        }

        throw new NotSupportedException(
            $"The loopback device offered {format.Encoding} at {format.BitsPerSample}-bit; "
            + "Monad can read 32-bit float or 16-bit PCM.");
    }

    internal static float[] Convert(ReadOnlySpan<byte> bytes, WaveFormat format, AudioClientBufferFlags flags)
    {
        format = GetSupportedFormat(format);
        bytes = bytes[..(bytes.Length - (bytes.Length % format.BlockAlign))];

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            return new float[bytes.Length / (format.BitsPerSample / 8)];
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }

        ReadOnlySpan<short> source = MemoryMarshal.Cast<byte, short>(bytes);
        float[] samples = new float[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            samples[i] = source[i] / 32768f;
        }

        return samples;
    }

    public async ValueTask DisposeAsync()
    {
        WasapiRecorder? capture = Interlocked.Exchange(ref this.capture, null);

        if (capture is not null)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
