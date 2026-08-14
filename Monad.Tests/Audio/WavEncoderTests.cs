using System;
using System.Buffers.Binary;
using System.Text;

using FluentAssertions;

using Monad.Audio;

using Xunit;

namespace Monad.Tests.Audio;

public sealed class WavEncoderTests
{
    private const int HeaderBytes = 44;

    [Fact]
    public void ToWavPcm16Mono_WritesAWellFormedRiffHeader()
    {
        // Arrange / Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(new float[100], sampleRate: 44100);

        // Assert
        Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        Encoding.ASCII.GetString(wav, 12, 4).Should().Be("fmt ");
        Encoding.ASCII.GetString(wav, 36, 4).Should().Be("data");

        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)).Should().Be(wav.Length - 8);
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)).Should().Be(16); // PCM chunk size
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)).Should().Be(1); // PCM tag
    }

    [Fact]
    public void ToWavPcm16Mono_DescribesMono16BitAudioAtTheGivenRate()
    {
        // Arrange / Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(new float[10], sampleRate: 48000);

        // Assert
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)).Should().Be(1); // channels
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)).Should().Be(48000); // sample rate
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)).Should().Be(48000 * 2); // byte rate
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)).Should().Be(2); // block align
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)).Should().Be(16); // bits per sample
    }

    [Fact]
    public void ToWavPcm16Mono_SizesTheDataChunkFromTheSampleCount()
    {
        // Arrange / Act
        byte[] wav = WavEncoder.ToWavPcm16Mono(new float[256], sampleRate: 44100);

        // Assert
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)).Should().Be(512);
        wav.Length.Should().Be(HeaderBytes + 512);
    }

    [Fact]
    public void ToWavPcm16Mono_WithNoSamples_WritesHeaderOnly()
    {
        // Arrange / Act
        byte[] wav = WavEncoder.ToWavPcm16Mono([], sampleRate: 44100);

        // Assert
        wav.Length.Should().Be(HeaderBytes);
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)).Should().Be(0);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32767)]
    [InlineData(0.5f, 16384)] // 0.5 * 32767 = 16383.5, rounded away from zero
    [InlineData(-0.5f, -16384)]
    public void ToWavPcm16Mono_ScalesSamplesToSignedSixteenBit(float sample, short expected)
    {
        // Arrange / Act
        byte[] wav = WavEncoder.ToWavPcm16Mono([sample], sampleRate: 8000);

        // Assert
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(HeaderBytes)).Should().Be(expected);
    }

    [Theory]
    [InlineData(2.5f, 32767)]
    [InlineData(-2.5f, -32767)]
    public void ToWavPcm16Mono_ClampsSamplesOutsideFullScale(float sample, short expected)
    {
        // Arrange / Act — float audio can exceed ±1; it must clamp, not wrap
        byte[] wav = WavEncoder.ToWavPcm16Mono([sample], sampleRate: 8000);

        // Assert
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(HeaderBytes)).Should().Be(expected);
    }

    [Fact]
    public void ToWavPcm16Mono_WithInvalidSampleRate_Throws()
    {
        // Arrange / Act
        Action act = () => WavEncoder.ToWavPcm16Mono([0f], sampleRate: 0);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
