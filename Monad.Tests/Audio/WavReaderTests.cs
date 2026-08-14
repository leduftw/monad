using System;
using System.Buffers.Binary;
using System.IO;

using FluentAssertions;

using Monad.Audio;

using Xunit;

namespace Monad.Tests.Audio;

public sealed class WavReaderTests
{
    [Fact]
    public void ReadMono_RoundTripsWhatWavEncoderWrote()
    {
        // Arrange
        float[] original = [0f, 0.5f, -0.5f, 0.25f];
        byte[] wav = WavEncoder.ToWavPcm16Mono(original, sampleRate: 44100);

        // Act
        DecodedAudio decoded = WavReader.ReadMono(new MemoryStream(wav));

        // Assert — 16-bit quantisation costs a little precision, nothing more
        decoded.SampleRate.Should().Be(44100);
        decoded.Mono.Should().HaveCount(4);

        for (int i = 0; i < original.Length; i++)
        {
            decoded.Mono[i].Should().BeApproximately(original[i], 1e-4f);
        }
    }

    [Fact]
    public void ReadMono_WithStereoInput_AveragesToMono()
    {
        // Arrange — one frame: left 1.0, right 0.0
        byte[] wav = BuildWav(format: 1, channels: 2, sampleRate: 48000, bitsPerSample: 16, data:
        [
            .. BitConverter.GetBytes((short)32767),
            .. BitConverter.GetBytes((short)0),
        ]);

        // Act
        DecodedAudio decoded = WavReader.ReadMono(new MemoryStream(wav));

        // Assert
        decoded.Mono.Should().HaveCount(1);
        decoded.Mono[0].Should().BeApproximately(0.5f, 1e-3f);
    }

    [Fact]
    public void ReadMono_WithFloat32Input_ReadsSamplesDirectly()
    {
        // Arrange
        byte[] wav = BuildWav(format: 3, channels: 1, sampleRate: 48000, bitsPerSample: 32, data:
        [
            .. BitConverter.GetBytes(0.75f),
            .. BitConverter.GetBytes(-0.25f),
        ]);

        // Act
        DecodedAudio decoded = WavReader.ReadMono(new MemoryStream(wav));

        // Assert
        decoded.Mono.Should().HaveCount(2);
        decoded.Mono[0].Should().BeApproximately(0.75f, 1e-6f);
        decoded.Mono[1].Should().BeApproximately(-0.25f, 1e-6f);
    }

    [Fact]
    public void ReadMono_SkipsUnknownChunks()
    {
        // Arrange — a LIST chunk between 'fmt ' and 'data', as real files carry
        byte[] wav = BuildWav(
            format: 1,
            channels: 1,
            sampleRate: 8000,
            bitsPerSample: 16,
            data: [.. BitConverter.GetBytes((short)16384)],
            extraChunk: ("LIST", [0x41, 0x42, 0x43, 0x44]));

        // Act
        DecodedAudio decoded = WavReader.ReadMono(new MemoryStream(wav));

        // Assert
        decoded.Mono.Should().HaveCount(1);
        decoded.Mono[0].Should().BeApproximately(0.5f, 1e-3f);
    }

    [Fact]
    public void Duration_ReflectsSampleCountAndRate()
    {
        // Arrange / Act
        DecodedAudio audio = new(new float[48000], 48000);

        // Assert
        audio.Duration.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadMono_WithNonRiffData_Throws()
    {
        // Arrange / Act
        Action act = () => WavReader.ReadMono(new MemoryStream(new byte[64]));

        // Assert
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReadMono_WithUnsupportedEncoding_Throws()
    {
        // Arrange — 4-bit ADPCM is not something Monad needs to read
        byte[] wav = BuildWav(format: 2, channels: 1, sampleRate: 8000, bitsPerSample: 4, data: [0x11, 0x22]);

        // Act
        Action act = () => WavReader.ReadMono(new MemoryStream(wav));

        // Assert
        act.Should().Throw<InvalidDataException>();
    }

    private static byte[] BuildWav(
        ushort format,
        ushort channels,
        int sampleRate,
        ushort bitsPerSample,
        byte[] data,
        (string Id, byte[] Payload)? extraChunk = null)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8);
        writer.Write(0); // patched below
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write(format);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);

        if (extraChunk is { } extra)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(extra.Id));
            writer.Write(extra.Payload.Length);
            writer.Write(extra.Payload);
        }

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);

        return bytes;
    }
}
