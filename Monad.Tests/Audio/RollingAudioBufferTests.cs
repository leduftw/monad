using System;

using FluentAssertions;

using Monad.Audio;

using Xunit;

namespace Monad.Tests.Audio;

public sealed class RollingAudioBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const int Rate = 100; // 100 Hz keeps the arithmetic readable

    private static RollingAudioBuffer Create(double capacitySeconds = 1.0) =>
        new(Rate, TimeSpan.FromSeconds(capacitySeconds));

    private static float[] Constant(int count, float value)
    {
        float[] samples = new float[count];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void CopyLatest_BeforeAnyAudio_ReturnsSilence()
    {
        // Arrange
        RollingAudioBuffer buffer = Create();
        float[] window = new float[50];

        // Act
        AudioWindow slice = buffer.CopyLatest(window, T0);

        // Assert
        buffer.HasReceivedAudio.Should().BeFalse();
        window.Should().AllSatisfy(sample => sample.Should().Be(0f));
        slice.SampleRate.Should().Be(Rate);
    }

    [Fact]
    public void AppendInterleaved_DownmixesToMono()
    {
        // Arrange — two stereo frames: (1.0, 0.0) and (0.5, 0.5)
        RollingAudioBuffer buffer = Create();
        float[] stereo = [1.0f, 0.0f, 0.5f, 0.5f];

        // Act
        buffer.AppendInterleaved(stereo, channels: 2, T0.AddMilliseconds(20));
        float[] window = new float[2];
        buffer.CopyLatest(window, T0.AddMilliseconds(20));

        // Assert
        window[0].Should().BeApproximately(0.5f, 1e-6f);
        window[1].Should().BeApproximately(0.5f, 1e-6f);
        buffer.HasReceivedAudio.Should().BeTrue();
    }

    [Fact]
    public void CopyLatest_PutsTheNewestSampleAtTheEndOfTheWindow()
    {
        // Arrange — samples 1..4 appended in order
        RollingAudioBuffer buffer = Create();
        buffer.AppendInterleaved([0.1f, 0.2f, 0.3f, 0.4f], channels: 1, T0.AddMilliseconds(40));

        // Act — ask for a window twice as long as the audio available
        float[] window = new float[8];
        buffer.CopyLatest(window, T0.AddMilliseconds(40));

        // Assert — right-aligned, with the missing history zero-filled in front
        window[..4].Should().AllSatisfy(sample => sample.Should().Be(0f));
        window[4..].Should().Equal(0.1f, 0.2f, 0.3f, 0.4f);
    }

    [Fact]
    public void CopyLatest_KeepsOnlyTheMostRecentAudioOnceCapacityIsExceeded()
    {
        // Arrange — capacity is 1s = 100 samples; write 150
        RollingAudioBuffer buffer = Create(capacitySeconds: 1.0);

        buffer.AppendInterleaved(Constant(100, 0.1f), channels: 1, T0.AddSeconds(1));
        buffer.AppendInterleaved(Constant(50, 0.9f), channels: 1, T0.AddSeconds(1.5));

        // Act
        float[] window = new float[100];
        buffer.CopyLatest(window, T0.AddSeconds(1.5));

        // Assert — the newest 50 samples are the 0.9s, ahead of them the tail of the 0.1s
        window[^1].Should().BeApproximately(0.9f, 1e-6f);
        window[^50].Should().BeApproximately(0.9f, 1e-6f);
        window[^51].Should().BeApproximately(0.1f, 1e-6f);
    }

    [Fact]
    public void CopyLatest_WrapsCorrectlyAroundTheRing()
    {
        // Arrange — several writes that cross the ring boundary repeatedly
        RollingAudioBuffer buffer = Create(capacitySeconds: 1.0); // 100 samples

        for (int i = 0; i < 10; i++)
        {
            buffer.AppendInterleaved(Constant(30, i), channels: 1, T0.AddSeconds(0.3 * (i + 1)));
        }

        // Act
        float[] window = new float[100];
        buffer.CopyLatest(window, T0.AddSeconds(3.0));

        // Assert — last write was thirty 9s, before that thirty 8s
        window[^1].Should().Be(9f);
        window[^30].Should().Be(9f);
        window[^31].Should().Be(8f);
    }

    // --- Silence padding: the behaviour the whole class exists for ---

    [Fact]
    public void CopyLatest_AfterALongQuietStretch_ReportsSilenceRatherThanStaleAudio()
    {
        // Arrange — audio, then nothing delivered for a full window's worth of
        // time. macOS stops delivering buffers entirely when nothing plays, so
        // without padding the buffer would keep replaying the last thing heard.
        RollingAudioBuffer buffer = Create(capacitySeconds: 2.0);
        buffer.AppendInterleaved(Constant(100, 0.8f), channels: 1, T0.AddSeconds(1));

        // Act — ask a full second later, with nothing appended in between
        float[] window = new float[100];
        buffer.CopyLatest(window, T0.AddSeconds(2));

        // Assert
        window.Should().AllSatisfy(sample => sample.Should().Be(0f));
        AudioProcessing.RmsDbfs(window).Should().Be(AudioProcessing.SilenceFloorDbfs);
    }

    [Fact]
    public void AppendInterleaved_FillsAGapBetweenBlocksWithSilence()
    {
        // Arrange — 0.2s of tone, half a second of nothing, then more tone
        RollingAudioBuffer buffer = Create(capacitySeconds: 2.0);
        buffer.AppendInterleaved(Constant(20, 0.5f), channels: 1, T0.AddSeconds(0.2));
        buffer.AppendInterleaved(Constant(20, 0.5f), channels: 1, T0.AddSeconds(0.9));

        // Act
        float[] window = new float[90];
        buffer.CopyLatest(window, T0.AddSeconds(0.9));

        // Assert — newest 20 are tone, the 50 before them are the silent gap
        window[^1].Should().BeApproximately(0.5f, 1e-6f);
        window[^20].Should().BeApproximately(0.5f, 1e-6f);
        window[^21].Should().Be(0f);
        window[^70].Should().Be(0f);
    }

    [Fact]
    public void AppendInterleaved_TreatsNormalDeliveryJitterAsContinuousAudio()
    {
        // Arrange — blocks arriving a few ms late must not be padded, or every
        // window would be shot through with spurious silence.
        RollingAudioBuffer buffer = Create(capacitySeconds: 2.0);

        DateTimeOffset at = T0;
        for (int i = 0; i < 10; i++)
        {
            at = at.AddMilliseconds(100 + (i % 3 * 10)); // 100-120ms of jitter
            buffer.AppendInterleaved(Constant(10, 0.5f), channels: 1, at);
        }

        // Act
        float[] window = new float[100];
        buffer.CopyLatest(window, at);

        // Assert
        window.Should().AllSatisfy(sample => sample.Should().BeApproximately(0.5f, 1e-6f));
    }

    [Fact]
    public void Reset_DiscardsEverythingHeld()
    {
        // Arrange
        RollingAudioBuffer buffer = Create();
        buffer.AppendInterleaved(Constant(50, 0.5f), channels: 1, T0.AddSeconds(0.5));

        // Act
        buffer.Reset();
        float[] window = new float[50];
        buffer.CopyLatest(window, T0.AddSeconds(0.5));

        // Assert
        buffer.HasReceivedAudio.Should().BeFalse();
        window.Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public void CopyLatest_ReportsTheWindowItCovers()
    {
        // Arrange
        RollingAudioBuffer buffer = Create(capacitySeconds: 2.0);
        DateTimeOffset arrived = T0.AddSeconds(1);
        buffer.AppendInterleaved(Constant(100, 0.5f), channels: 1, arrived);

        // Act — 50 samples at 100 Hz is half a second
        AudioWindow slice = buffer.CopyLatest(new float[50], arrived);

        // Assert
        slice.EndUtc.Should().Be(arrived);
        slice.StartUtc.Should().Be(arrived.AddSeconds(-0.5));
        slice.SampleCount.Should().Be(50);
    }

    [Fact]
    public void Constructor_WithInvalidArguments_Throws()
    {
        FluentActions.Invoking(() => new RollingAudioBuffer(0, TimeSpan.FromSeconds(1)))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => new RollingAudioBuffer(48000, TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
