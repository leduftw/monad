using System;
using System.IO;

using NAudio.Wave;

namespace Monad.Audio;

public static class WavEncoder
{
    public static byte[] ToWavPcm16Mono(short[] mono, int sampleRate)
    {
        using MemoryStream ms = new();

        using (WaveFileWriter writer = new(ms, new WaveFormat(sampleRate, 16, 1)))
        {
            byte[] buf = new byte[mono.Length * 2];
            Buffer.BlockCopy(mono, 0, buf, 0, buf.Length);
            writer.Write(buf, 0, buf.Length);
            writer.Flush();
        }

        return ms.ToArray();
    }
}
