using FluentAssertions;
using Monad.Audio;
using Xunit;

namespace Monad.Tests.Audio;

public sealed class AudioProcessingTests
{
    // --- DownmixToMono ---

    [Fact]
    public void DownmixToMono_WithMonoInput_ReturnsSameReference()
    {
        // Arrange
        short[] input = [10, 20, 30];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 1);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void DownmixToMono_WithZeroChannels_ReturnsSameReference()
    {
        // Arrange
        short[] input = [10, 20, 30];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 0);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void DownmixToMono_WithStereoInput_AveragesChannels()
    {
        // Arrange
        short[] input = [100, 200, -50, 50];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 2);

        // Assert
        result.Should().Equal((short)150, (short)0);
    }

    [Fact]
    public void DownmixToMono_WithSixChannels_AveragesAllChannels()
    {
        // Arrange — 6 channels, 1 frame: sum=900, avg=150
        short[] input = [600, 300, 0, -300, 150, 150];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 6);

        // Assert
        result.Should().Equal((short)150);
    }

    [Fact]
    public void DownmixToMono_WithMaxPositiveValues_ClampsToShortMax()
    {
        // Arrange
        short[] input = [short.MaxValue, short.MaxValue];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 2);

        // Assert
        result.Should().Equal(short.MaxValue);
    }

    [Fact]
    public void DownmixToMono_WithMaxNegativeValues_ClampsToShortMin()
    {
        // Arrange
        short[] input = [short.MinValue, short.MinValue];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 2);

        // Assert
        result.Should().Equal(short.MinValue);
    }

    [Fact]
    public void DownmixToMono_WithEmptyArray_ReturnsEmptyArray()
    {
        // Arrange
        short[] input = [];

        // Act
        short[] result = AudioProcessing.DownmixToMono(input, channels: 2);

        // Assert
        result.Should().BeEmpty();
    }

    // --- NormalizeOnlyBoost ---

    [Fact]
    public void NormalizeOnlyBoost_WithEmptyArray_ReturnsSameReference()
    {
        // Arrange
        short[] input = [];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 0.9f);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void NormalizeOnlyBoost_WithAllZeros_ReturnsSameReference()
    {
        // Arrange
        short[] input = [0, 0, 0];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 0.9f);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void NormalizeOnlyBoost_WithPeakAtTarget_ReturnsSameReference()
    {
        // Arrange — peak = 32767, target = 1.0 → scale = 1.0
        short[] input = [short.MaxValue, 0, -100];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 1.0f);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void NormalizeOnlyBoost_WithPeakAboveTarget_ReturnsSameReference()
    {
        // Arrange — peak = 32767, target = 0.5 → scale < 1.0 → no attenuation
        short[] input = [short.MaxValue, 0];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 0.5f);

        // Assert
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void NormalizeOnlyBoost_WithPeakBelowTarget_BoostsValues()
    {
        // Arrange — peak = 1000, target = 1.0 → targetPeak = 32767, scale = 32.767
        // value 1000 * 32.767 = 32767
        short[] input = [1000];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 1.0f);

        // Assert
        result.Should().NotBeSameAs(input);
        result[0].Should().Be(short.MaxValue);
    }

    [Fact]
    public void NormalizeOnlyBoost_WithBoostCausingOverflow_ClampsToShortRange()
    {
        // Arrange — peak = 100, target = 1.0 → scale = 327.67
        // value -100 * 327.67 = -32767
        short[] input = [100, -100];

        // Act
        short[] result = AudioProcessing.NormalizeOnlyBoost(input, peakTarget: 1.0f);

        // Assert
        result[0].Should().BeGreaterThanOrEqualTo(short.MinValue);
        result[0].Should().BeLessThanOrEqualTo(short.MaxValue);
        result[1].Should().BeGreaterThanOrEqualTo(short.MinValue);
        result[1].Should().BeLessThanOrEqualTo(short.MaxValue);
    }

    // --- RmsDbfs ---

    [Fact]
    public void RmsDbfs_WithEmptyArray_ReturnsNegative999()
    {
        // Arrange
        short[] input = [];

        // Act
        double result = AudioProcessing.RmsDbfs(input);

        // Assert
        result.Should().Be(-999.0);
    }

    [Fact]
    public void RmsDbfs_WithSilence_ReturnsNegative999()
    {
        // Arrange
        short[] input = [0, 0, 0, 0];

        // Act
        double result = AudioProcessing.RmsDbfs(input);

        // Assert
        result.Should().Be(-999.0);
    }

    [Fact]
    public void RmsDbfs_WithFullScaleSignal_ReturnsApproximatelyZeroDbfs()
    {
        // Arrange — constant signal at max value: RMS = 32767/32768 ≈ ~0 dBFS
        short[] input = [short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue];

        // Act
        double result = AudioProcessing.RmsDbfs(input);

        // Assert
        result.Should().BeApproximately(0.0, 0.1);
    }

    [Fact]
    public void RmsDbfs_WithKnownSignal_ReturnsExpectedLevel()
    {
        // Arrange — value 3277 ≈ 10% of 32768 → RMS = 3277/32768 ≈ 0.1 → 20*log10(0.1) ≈ -20 dBFS
        short[] input = [3277, 3277, 3277, 3277];

        // Act
        double result = AudioProcessing.RmsDbfs(input);

        // Assert
        result.Should().BeApproximately(-20.0, 0.1);
    }
}
