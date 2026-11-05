using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Audio;
using Monad.Audio.Sources;

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Monad.Tests.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackSourceTests
{
    [WindowsFact]
    public void Convert_WithFloatPacket_CopiesInterleavedSamples()
    {
        float[] input = [0.25f, -0.5f, 0.75f, -1f];
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), format, AudioClientBufferFlags.None);
        input[0] = 0f;

        samples.Should().Equal(0.25f, -0.5f, 0.75f, -1f);
    }

    [WindowsFact]
    public void Convert_WithPcm16Packet_NormalizesSamples()
    {
        short[] input = [short.MinValue, 0, 16384, short.MaxValue];

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), new WaveFormat(44100, 16, 2), AudioClientBufferFlags.None);

        samples.Should().Equal(-1f, 0f, 0.5f, 32767f / 32768f);
    }

    [WindowsFact]
    public void Convert_WithExtensibleFloatPacket_ReadsTheDeviceMixFormat()
    {
        float[] input = [0.25f, -0.75f];

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), new WaveFormatExtensible(44100, 32, 2),
            AudioClientBufferFlags.None);

        samples.Should().Equal(input);
    }

    [WindowsFact]
    public void Convert_WithExtensiblePcmPacket_NormalizesSamples()
    {
        short[] input = [short.MinValue, 16384];

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), new WaveFormatExtensible(48000, 16, 2),
            AudioClientBufferFlags.None);

        samples.Should().Equal(-1f, 0.5f);
    }

    [WindowsFact]
    public void Convert_WithSilentPacket_DoesNotLeakStaleAudio()
    {
        float[] input = [1f, -1f, 0.5f, -0.5f];

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            AudioClientBufferFlags.Silent | AudioClientBufferFlags.DataDiscontinuity);

        samples.Should().Equal(0f, 0f, 0f, 0f);
    }

    [WindowsFact]
    public void Convert_WithIncompleteTrailingFrame_IgnoresIt()
    {
        float[] input = [0.25f, -0.5f, 0.75f];

        float[] samples = WasapiLoopbackSource.Convert(
            MemoryMarshal.AsBytes(input.AsSpan()), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            AudioClientBufferFlags.None);

        samples.Should().Equal(0.25f, -0.5f);
    }

    [WindowsFact]
    public void GetSupportedFormat_WithPcm24_RejectsRatherThanMisreadsAudio()
    {
        Action read = () => WasapiLoopbackSource.GetSupportedFormat(new WaveFormat(48000, 24, 2));

        read.Should().Throw<NotSupportedException>().WithMessage("*24-bit*");
    }

    [WindowsFact]
    public async Task ReadAsync_WhenAlreadyCanceled_ReportsCancellation()
    {
        await using WasapiLoopbackSource source = new(_ => { });
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

        Func<Task> read = async () => await blocks.MoveNextAsync();

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
