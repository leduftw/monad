using System;
using System.Text;
using FluentAssertions;
using Monad.Audio;
using Xunit;

namespace Monad.Tests.Audio;

public sealed class WavEncoderTests
{
    [Fact]
    public void ToWavPcm16Mono_WithValidInput_HasCorrectRiffHeader()
    {
        // Arrange
        short[] samples = [100, -100, 200];

        // Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(samples, sampleRate: 44100);

        // Assert
        string riff = Encoding.ASCII.GetString(wav, 0, 4);
        string wave = Encoding.ASCII.GetString(wav, 8, 4);
        riff.Should().Be("RIFF");
        wave.Should().Be("WAVE");
    }

    [Fact]
    public void ToWavPcm16Mono_WithValidInput_HasCorrectDataChunkSize()
    {
        // Arrange
        short[] samples = [100, -100, 200];
        int expectedDataSize = samples.Length * 2; // 16-bit = 2 bytes per sample

        // Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(samples, sampleRate: 44100);

        // Assert — find "data" marker and read size from next 4 bytes
        int dataOffset = FindMarker(wav, "data");
        dataOffset.Should().BeGreaterThan(0);
        int dataSize = BitConverter.ToInt32(wav, dataOffset + 4);
        dataSize.Should().Be(expectedDataSize);
    }

    [Fact]
    public void ToWavPcm16Mono_WithValidInput_HasCorrectSampleRate()
    {
        // Arrange
        int sampleRate = 16000;

        // Act
        byte[] wav = WavEncoder.ToWavPcm16Mono([1000], sampleRate);

        // Assert — sample rate is at bytes 24-27 in the WAV header
        int encoded = BitConverter.ToInt32(wav, 24);
        encoded.Should().Be(sampleRate);
    }

    [Fact]
    public void ToWavPcm16Mono_WithEmptySamples_ProducesHeaderOnlyOutput()
    {
        // Arrange
        short[] samples = [];

        // Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(samples, sampleRate: 44100);

        // Assert
        int dataOffset = FindMarker(wav, "data");
        dataOffset.Should().BeGreaterThan(0);
        int dataSize = BitConverter.ToInt32(wav, dataOffset + 4);
        dataSize.Should().Be(0);
    }

    [Fact]
    public void ToWavPcm16Mono_WithKnownSample_EncodesCorrectPcm16Bytes()
    {
        // Arrange — sample value 0x0190 (400) in little-endian: 0x90, 0x01
        short[] samples = [400];

        // Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(samples, sampleRate: 44100);

        // Assert — data starts after the "data" marker + 4-byte size
        int dataOffset = FindMarker(wav, "data") + 8;
        wav[dataOffset].Should().Be(0x90);
        wav[dataOffset + 1].Should().Be(0x01);
    }

    private static int FindMarker(byte[] wav, string marker)
    {
        byte[] target = Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i <= wav.Length - target.Length; i++)
        {
            if (wav[i] == target[0] && wav[i + 1] == target[1] &&
                wav[i + 2] == target[2] && wav[i + 3] == target[3])
            {
                return i;
            }
        }

        return -1;
    }
}
