using System;

namespace Monad.Audio;

/// <summary>The stretch of wall-clock time a copied window covers.</summary>
public readonly record struct AudioWindow(
    int SampleCount,
    int SampleRate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>
/// A fixed-length, wall-clock-aligned ring of recent mono audio.
/// </summary>
/// <remarks>
/// System audio capture goes quiet in a very literal way: when nothing is
/// playing, the OS stops delivering buffers rather than delivering zeros. A
/// plain ring buffer would therefore keep replaying the last thing it heard
/// forever. This buffer tracks the wall-clock time of its newest sample and
/// pads real silence into any gap, so a window always describes the period it
/// claims to.
/// </remarks>
public sealed class RollingAudioBuffer
{
    /// <summary>
    /// Delivery jitter shorter than this is normal and is treated as continuous
    /// audio. A longer gap means the source genuinely had nothing to hand over.
    /// </summary>
    public static readonly TimeSpan DefaultSilenceGap = TimeSpan.FromMilliseconds(150);

    private const int DownmixChunkFrames = 4096;

    private readonly object gate = new();
    private readonly float[] storage;
    private readonly float[] downmixScratch = new float[DownmixChunkFrames];
    private readonly TimeSpan silenceGap;

    private int writeIndex;
    private int available;
    private DateTimeOffset streamEndUtc;
    private bool started;

    public RollingAudioBuffer(int sampleRate, TimeSpan capacity, TimeSpan? silenceGap = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, TimeSpan.Zero);

        this.SampleRate = sampleRate;
        this.storage = new float[Math.Max(1, (int)(capacity.TotalSeconds * sampleRate))];
        this.silenceGap = silenceGap ?? DefaultSilenceGap;
    }

    public int SampleRate { get; }

    public int Capacity => this.storage.Length;

    /// <summary>True once any audio at all has been delivered.</summary>
    public bool HasReceivedAudio
    {
        get { lock (this.gate) { return this.started; } }
    }

    /// <summary>Downmixes interleaved audio to mono and appends it.</summary>
    /// <param name="arrivedUtc">When the last sample of the block was captured.</param>
    public void AppendInterleaved(ReadOnlySpan<float> interleaved, int channels, DateTimeOffset arrivedUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        int totalFrames = interleaved.Length / channels;
        if (totalFrames == 0)
        {
            return;
        }

        lock (this.gate)
        {
            this.PadGapBefore(arrivedUtc, totalFrames);

            for (int frame = 0; frame < totalFrames; frame += DownmixChunkFrames)
            {
                int frames = Math.Min(DownmixChunkFrames, totalFrames - frame);
                ReadOnlySpan<float> slice = interleaved.Slice(frame * channels, frames * channels);

                AudioProcessing.DownmixToMono(slice, channels, this.downmixScratch);
                this.WriteLocked(this.downmixScratch.AsSpan(0, frames));
            }

            this.streamEndUtc = arrivedUtc;
            this.started = true;
        }
    }

    /// <summary>
    /// Copies the most recent <c>destination.Length</c> samples, filling the
    /// front with silence when the buffer does not hold that much yet, and
    /// filling the back with silence for any time the source stayed quiet.
    /// </summary>
    public AudioWindow CopyLatest(Span<float> destination, DateTimeOffset nowUtc)
    {
        lock (this.gate)
        {
            this.PadSilenceUpTo(nowUtc);

            destination.Clear();

            int count = Math.Min(this.available, destination.Length);

            if (count > 0)
            {
                int readIndex = (this.writeIndex - count + this.storage.Length) % this.storage.Length;
                int toEnd = Math.Min(count, this.storage.Length - readIndex);

                // Right-align: the newest sample lands at the end of the window.
                Span<float> tail = destination[^count..];
                this.storage.AsSpan(readIndex, toEnd).CopyTo(tail);

                if (count > toEnd)
                {
                    this.storage.AsSpan(0, count - toEnd).CopyTo(tail[toEnd..]);
                }
            }

            DateTimeOffset end = this.started ? this.streamEndUtc : nowUtc;
            TimeSpan span = TimeSpan.FromSeconds((double)destination.Length / this.SampleRate);

            return new AudioWindow(destination.Length, this.SampleRate, end - span, end);
        }
    }

    /// <summary>Drops everything held, for instance when the source restarts with a new format.</summary>
    public void Reset()
    {
        lock (this.gate)
        {
            Array.Clear(this.storage);
            this.writeIndex = 0;
            this.available = 0;
            this.started = false;
        }
    }

    /// <summary>Fills the quiet stretch that preceded an arriving block.</summary>
    private void PadGapBefore(DateTimeOffset arrivedUtc, int frames)
    {
        DateTimeOffset blockStart = arrivedUtc - TimeSpan.FromSeconds((double)frames / this.SampleRate);

        if (!this.started)
        {
            this.streamEndUtc = blockStart;
            return;
        }

        this.PadSilenceUpTo(blockStart);
    }

    private void PadSilenceUpTo(DateTimeOffset instant)
    {
        if (!this.started)
        {
            return;
        }

        TimeSpan gap = instant - this.streamEndUtc;

        if (gap <= this.silenceGap)
        {
            return;
        }

        int samples = (int)Math.Min(gap.TotalSeconds * this.SampleRate, this.storage.Length);
        this.WriteSilenceLocked(samples);

        this.streamEndUtc = instant;
    }

    private void WriteSilenceLocked(int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, this.storage.Length - this.writeIndex);
            this.storage.AsSpan(this.writeIndex, chunk).Clear();
            this.AdvanceLocked(chunk);
            count -= chunk;
        }
    }

    private void WriteLocked(ReadOnlySpan<float> mono)
    {
        // A block longer than the ring can only leave its newest tail behind.
        if (mono.Length >= this.storage.Length)
        {
            mono[^this.storage.Length..].CopyTo(this.storage);
            this.writeIndex = 0;
            this.available = this.storage.Length;
            return;
        }

        int toEnd = Math.Min(mono.Length, this.storage.Length - this.writeIndex);
        mono[..toEnd].CopyTo(this.storage.AsSpan(this.writeIndex));

        if (mono.Length > toEnd)
        {
            mono[toEnd..].CopyTo(this.storage);
        }

        this.AdvanceLocked(mono.Length);
    }

    private void AdvanceLocked(int count)
    {
        this.writeIndex = (this.writeIndex + count) % this.storage.Length;
        this.available = Math.Min(this.available + count, this.storage.Length);
    }
}
