using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;
using Monad.Audio;
using Monad.Output;
using Monad.Recognition;

namespace Monad.Tests;

/// <summary>Replays a scripted sequence of outcomes and keeps what it was sent.</summary>
public sealed class FakeRecognizer(params RecognitionOutcome[] script) : ISongRecognizer
{
    private int index;

    public List<byte[]> Received { get; } = [];

    public int Calls => this.Received.Count;

    public Task<RecognitionOutcome> RecognizeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Received.Add(wavBytes);

        // Past the end of the script, keep answering with the last entry.
        RecognitionOutcome outcome = script.Length == 0
            ? RecognitionOutcome.NoMatch
            : script[Math.Min(this.index, script.Length - 1)];

        this.index++;

        return Task.FromResult(outcome);
    }

    public static RecognitionOutcome Match(string artist, string title, string isrc) =>
        RecognitionOutcome.Matched(new RecognitionResult(
            artist, title, IsrcInfo.None with { Selected = isrc, Suffix = $"[ISRC: {isrc}]" }));
}

/// <summary>Collects the segments a run produced.</summary>
public sealed class RecordingSegmentSink : ISegmentSink
{
    public List<Segment> Segments { get; } = [];

    public bool Disposed { get; private set; }

    public ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken)
    {
        this.Segments.Add(segment);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        this.Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Emits a fixed block of audio on a timer until cancelled.</summary>
public sealed class FakeAudioSource(float[] block, AudioFormat format, TimeSpan cadence) : ISystemAudioSource
{
    public string Description => "fake audio source";

    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<AudioBlock> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new AudioBlock(block, format);

            try
            {
                await Task.Delay(cadence, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        this.Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A square wave loud enough to read well above silence. It alternates per
    /// frame rather than per sample: alternating per sample would put the
    /// channels exactly out of phase, and the downmix would cancel them to
    /// digital silence.
    /// </summary>
    public static FakeAudioSource Tone(float amplitude, int sampleRate, int channels, TimeSpan cadence)
    {
        int frames = (int)(sampleRate * cadence.TotalSeconds);
        float[] samples = new float[frames * channels];

        for (int frame = 0; frame < frames; frame++)
        {
            float value = frame % 2 == 0 ? amplitude : -amplitude;

            for (int channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = value;
            }
        }

        return new FakeAudioSource(samples, new AudioFormat(sampleRate, channels), cadence);
    }
}
