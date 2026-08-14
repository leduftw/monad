using System;
using System.Buffers.Binary;
using System.IO;

namespace Monad.Audio;

/// <summary>Mono audio decoded from a WAV file.</summary>
public sealed record class DecodedAudio(float[] Mono, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)this.Mono.Length / this.SampleRate);
}

/// <summary>
/// Reads a WAV file down to mono float, for <c>monad replay</c>. Deliberately
/// small: it needs to handle the files people actually have lying around, not
/// every corner of the RIFF specification.
/// </summary>
public static class WavReader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static DecodedAudio ReadMono(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return ReadMono(stream);
    }

    public static DecodedAudio ReadMono(Stream stream)
    {
        using BinaryReader reader = new(stream);

        if (!Matches(reader.ReadBytes(4), "RIFF"u8))
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadUInt32(); // total size, unused

        if (!Matches(reader.ReadBytes(4), "WAVE"u8))
        {
            throw new InvalidDataException("RIFF file is not WAVE audio.");
        }

        ushort format = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            byte[] id = reader.ReadBytes(4);
            uint size = reader.ReadUInt32();

            if (id.Length < 4)
            {
                break;
            }

            if (Matches(id, "fmt "u8))
            {
                byte[] chunk = reader.ReadBytes((int)size);

                if (chunk.Length < 16)
                {
                    throw new InvalidDataException("Malformed 'fmt ' chunk.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(14));

                // WAVE_FORMAT_EXTENSIBLE hides the real encoding in a GUID
                // whose first two bytes are the classic format tag.
                if (format == FormatExtensible && chunk.Length >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(24));
                }
            }
            else if (Matches(id, "data"u8))
            {
                if (channels == 0 || sampleRate == 0)
                {
                    throw new InvalidDataException("'data' chunk appeared before 'fmt '.");
                }

                long remaining = stream.Length - stream.Position;
                byte[] data = reader.ReadBytes((int)Math.Min(size, (uint)Math.Max(remaining, 0)));

                return new DecodedAudio(Decode(data, format, channels, bitsPerSample), sampleRate);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current); // chunks are word-aligned
            }
        }

        throw new InvalidDataException("WAV file has no 'data' chunk.");
    }

    private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
        actual.Length == expected.Length && actual.SequenceEqual(expected);

    private static float[] Decode(ReadOnlySpan<byte> data, ushort format, ushort channels, ushort bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;

        if (bytesPerSample == 0)
        {
            throw new InvalidDataException($"Unsupported sample width: {bitsPerSample} bits.");
        }

        int frameBytes = bytesPerSample * channels;
        int frames = data.Length / frameBytes;
        float[] mono = new float[frames];

        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0.0;

            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> sample = data.Slice((frame * frameBytes) + (channel * bytesPerSample), bytesPerSample);
                sum += ReadSample(sample, format, bitsPerSample);
            }

            mono[frame] = (float)(sum / channels);
        }

        return mono;
    }

    private static double ReadSample(ReadOnlySpan<byte> sample, ushort format, ushort bitsPerSample) =>
        (format, bitsPerSample) switch
        {
            (FormatPcm, 8) => (sample[0] - 128) / 128.0, // 8-bit WAV is unsigned
            (FormatPcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
            (FormatPcm, 24) => ((sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608.0),
            (FormatPcm, 32) => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
            (FormatFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian(sample),
            (FormatFloat, 64) => BinaryPrimitives.ReadDoubleLittleEndian(sample),
            _ => throw new InvalidDataException(
                $"Unsupported WAV encoding: format tag {format}, {bitsPerSample}-bit."),
        };
}
