using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using NAudio.Wave;

namespace Monad.Audio;

public sealed class LoopbackRecorder : IAudioRecorder
{
    public async Task<RecordedAudio> RecordAsync(TimeSpan duration, CancellationToken ct)
    {
        using WasapiLoopbackCapture capture = new();

        int sampleRate = capture.WaveFormat.SampleRate;
        int channels = capture.WaveFormat.Channels;

        using MemoryStream ms = new();

        capture.DataAvailable += (_, e) => ms.Write(e.Buffer, 0, e.BytesRecorded);

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                tcs.TrySetException(e.Exception);
            }
            else
            {
                tcs.TrySetResult();
            }
        };

        capture.StartRecording();

        try
        {
            await Task.Delay(duration, ct);
        }
        finally
        {
            capture.StopRecording();
        }

        await tcs.Task;

        byte[] bytes = ms.ToArray();

        int floatCount = bytes.Length / 4;
        short[] pcm16 = new short[floatCount];

        for (int i = 0; i < floatCount; i++)
        {
            float f = BitConverter.ToSingle(bytes, i * 4);
            f = Math.Clamp(f, -1f, 1f);
            pcm16[i] = (short)Math.Round(f * 32767f);
        }

        return new RecordedAudio(pcm16, sampleRate, channels);
    }
}
