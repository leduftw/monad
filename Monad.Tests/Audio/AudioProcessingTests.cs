using System;

using FluentAssertions;

using Monad.Audio;

using Xunit;

namespace Monad.Tests.Audio;

public sealed class AudioProcessingTests
{
    // --- DownmixToMono ---

    [Fact]
    public void DownmixToMono_WithStereoInput_AveragesChannels()
    {
        // Arrange
        float[] interleaved = [1.0f, 0.0f, 0.5f, -0.5f];
        float[] mono = new float[2];

        // Act
        int frames = AudioProcessing.DownmixToMono(interleaved, channels: 2, mono);

        // Assert
        frames.Should().Be(2);
        mono[0].Should().BeApproximately(0.5f, 1e-6f);
        mono[1].Should().BeApproximately(0.0f, 1e-6f);
    }

    [Fact]
    public void DownmixToMono_WithMonoInput_CopiesUnchanged()
    {
        // Arrange
        float[] interleaved = [0.25f, -0.75f];
        float[] mono = new float[2];

        // Act
        int frames = AudioProcessing.DownmixToMono(interleaved, channels: 1, mono);

        // Assert
        frames.Should().Be(2);
        mono.Should().Equal(0.25f, -0.75f);
    }

    [Fact]
    public void DownmixToMono_WithSixChannels_AveragesAllChannels()
    {
        // Arrange — one frame of 5.1, values summing to 3.0
        float[] interleaved = [1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f];
        float[] mono = new float[1];

        // Act
        AudioProcessing.DownmixToMono(interleaved, channels: 6, mono);

        // Assert
        mono[0].Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void DownmixToMono_WithIncompleteTrailingFrame_IgnoresIt()
    {
        // Arrange — five samples of stereo is two whole frames plus a stray
        float[] interleaved = [1.0f, 1.0f, 0.5f, 0.5f, 0.9f];
        float[] mono = new float[4];

        // Act
        int frames = AudioProcessing.DownmixToMono(interleaved, channels: 2, mono);

        // Assert
        frames.Should().Be(2);
    }

    [Fact]
    public void DownmixToMono_WithEmptyInput_WritesNothing()
    {
        // Arrange / Act
        int frames = AudioProcessing.DownmixToMono([], channels: 2, new float[4]);

        // Assert
        frames.Should().Be(0);
    }

    [Fact]
    public void DownmixToMono_WithDestinationTooSmall_Throws()
    {
        // Arrange
        float[] interleaved = [1f, 1f, 1f, 1f];

        // Act
        Action act = () => AudioProcessing.DownmixToMono(interleaved, channels: 2, new float[1]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DownmixToMono_WithNonPositiveChannels_Throws(int channels)
    {
        // Arrange / Act
        Action act = () => AudioProcessing.DownmixToMono([1f], channels, new float[1]);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- RmsDbfs ---

    [Fact]
    public void RmsDbfs_WithEmptyInput_ReturnsSilenceFloor()
    {
        AudioProcessing.RmsDbfs([]).Should().Be(AudioProcessing.SilenceFloorDbfs);
    }

    [Fact]
    public void RmsDbfs_WithDigitalSilence_ReturnsSilenceFloor()
    {
        AudioProcessing.RmsDbfs(new float[512]).Should().Be(AudioProcessing.SilenceFloorDbfs);
    }

    [Fact]
    public void RmsDbfs_WithFullScaleSquareWave_ReturnsApproximatelyZero()
    {
        // Arrange — alternating ±1 has an RMS of exactly 1.0
        float[] samples = new float[256];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? 1f : -1f;
        }

        // Act / Assert
        AudioProcessing.RmsDbfs(samples).Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void RmsDbfs_WithHalfScaleSignal_ReturnsAboutMinusSixDb()
    {
        // Arrange — RMS of 0.5 is 20*log10(0.5) ≈ -6.02 dBFS
        float[] samples = new float[256];
        Array.Fill(samples, 0.5f);

        // Act / Assert
        AudioProcessing.RmsDbfs(samples).Should().BeApproximately(-6.02, 0.01);
    }

    [Fact]
    public void RmsDbfs_NeverReportsBelowTheSilenceFloor()
    {
        // Arrange — quieter than 16-bit can represent
        float[] samples = new float[64];
        Array.Fill(samples, 1e-9f);

        // Act / Assert — a finite floor keeps comparisons and JSON output sane
        AudioProcessing.RmsDbfs(samples).Should().Be(AudioProcessing.SilenceFloorDbfs);
    }

    // --- Peak ---

    [Fact]
    public void Peak_ReturnsLargestMagnitude()
    {
        AudioProcessing.Peak([0.1f, -0.8f, 0.3f]).Should().BeApproximately(0.8f, 1e-6f);
    }

    [Fact]
    public void Peak_WithEmptyInput_ReturnsZero()
    {
        AudioProcessing.Peak([]).Should().Be(0f);
    }

    // --- BoostToPeak ---

    [Fact]
    public void BoostToPeak_WithQuietAudio_ScalesUpToTarget()
    {
        // Arrange
        float[] samples = [0.1f, -0.05f];

        // Act
        AudioProcessing.BoostToPeak(samples, peakTarget: 0.9f);

        // Assert
        AudioProcessing.Peak(samples).Should().BeApproximately(0.9f, 1e-5f);
        samples[1].Should().BeApproximately(-0.45f, 1e-5f); // ratio preserved
    }

    [Fact]
    public void BoostToPeak_WithAudioAlreadyLouderThanTarget_LeavesItAlone()
    {
        // Arrange — boosting only ever raises quiet audio, so it cannot clip
        float[] samples = [0.95f, -0.5f];
        float[] original = (float[])samples.Clone();

        // Act
        AudioProcessing.BoostToPeak(samples, peakTarget: 0.9f);

        // Assert
        samples.Should().Equal(original);
    }

    [Fact]
    public void BoostToPeak_WithDigitalSilence_LeavesItAlone()
    {
        // Arrange
        float[] samples = new float[8];

        // Act
        AudioProcessing.BoostToPeak(samples, peakTarget: 0.9f);

        // Assert
        samples.Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public void BoostToPeak_NeverExceedsFullScale()
    {
        // Arrange
        float[] samples = [0.001f, -0.001f];

        // Act
        AudioProcessing.BoostToPeak(samples, peakTarget: 1.0f);

        // Assert
        samples.Should().AllSatisfy(sample => Math.Abs(sample).Should().BeLessThanOrEqualTo(1f));
    }

    [Fact]
    public void BoostToPeak_WithNonPositiveTarget_Throws()
    {
        // Arrange / Act
        Action act = () => AudioProcessing.BoostToPeak(new float[4], peakTarget: 0f);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BoostToPeak_DoesNotChangeTheLevelOfAlreadyLoudAudio()
    {
        // Arrange — guards the ordering bug this pipeline used to have: levels
        // must be measured before boosting, because boosting rewrites them.
        float[] quiet = new float[256];
        Array.Fill(quiet, 0.001f);

        double before = AudioProcessing.RmsDbfs(quiet);
        AudioProcessing.BoostToPeak(quiet, peakTarget: 0.9f);
        double after = AudioProcessing.RmsDbfs(quiet);

        // Assert — nearly 60 dB of difference, which is why the order matters
        before.Should().BeLessThan(-55.0);
        after.Should().BeGreaterThan(-2.0);
    }
}
