using System;
using System.Buffers.Binary;

namespace Monad.Audio;

/// <summary>
/// Encodes mono float samples as a 16-bit PCM WAV file, which is what the
/// recognition API accepts. Hand-rolled rather than pulled from an audio
/// library so the encoder is available on every platform.
/// </summary>
public static class WavEncoder
{
    private const int HeaderBytes = 44;
    private const short PcmFormatTag = 1;
    private const short BitsPerSample = 16;

    public static byte[] ToWavPcm16Mono(ReadOnlySpan<float> mono, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        int dataBytes = mono.Length * sizeof(short);
        byte[] wav = new byte[HeaderBytes + dataBytes];
        Span<byte> span = wav;

        WriteHeader(span, sampleRate, dataBytes);

        Span<byte> samples = span[HeaderBytes..];

        for (int i = 0; i < mono.Length; i++)
        {
            // 32767 rather than 32768: scaling by the negative-side magnitude
            // would clip every sample that sits at +1.0 full scale.
            float scaled = Math.Clamp(mono[i], -1f, 1f) * 32767f;
            short value = (short)Math.Round(scaled, MidpointRounding.AwayFromZero);

            BinaryPrimitives.WriteInt16LittleEndian(samples[(i * sizeof(short))..], value);
        }

        return wav;
    }

    private static void WriteHeader(Span<byte> span, int sampleRate, int dataBytes)
    {
        const short channels = 1;
        int byteRate = sampleRate * channels * (BitsPerSample / 8);
        short blockAlign = channels * (BitsPerSample / 8);

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderBytes - 8 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);

        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // PCM chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], PcmFormatTag);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BitsPerSample);

        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
    }
}
