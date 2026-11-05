using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Audio;
using Monad.Audio.Sources;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using Xunit.Abstractions;

namespace Monad.Tests.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackIntegrationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [WindowsAudioFact]
    public async Task ReadAsync_WithGeneratedTone_CapturesCorrectFrequencyAndFormat()
    {
        using MMDeviceEnumerator devices = new();
        using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using AudioClient client = device.CreateAudioClient();
        WaveFormat expected = client.MixFormat;
        await using WasapiPlayer player = CreatePlayer(device, gain: 0.1);
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);

        Task<(float[] Samples, AudioFormat Format)> capture = ReadToneAsync(source, stop.Token);
        player.Play();
        (float[] samples, AudioFormat format) = await capture.WaitAsync(TestTimeout);
        double frequency = EstimateFrequency(samples, format.SampleRate);
        output.WriteLine(
            $"{device.FriendlyName}: {samples.Length} mono frames at {format.SampleRate} Hz; tone {frequency:F2} Hz.");

        format.SampleRate.Should().Be(expected.SampleRate);
        format.Channels.Should().Be(expected.Channels);
        AudioProcessing.RmsDbfs(samples).Should().BeGreaterThan(-50);
        frequency.Should().BeApproximately(440, 3);
    }

    [WindowsAudioFact]
    public async Task ReadAsync_WhenCanceledDuringPlayback_StopsPromptly()
    {
        using MMDeviceEnumerator devices = new();
        using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        await using WasapiPlayer player = CreatePlayer(device, gain: 0.1);
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

        Task<bool> first = blocks.MoveNextAsync().AsTask();
        player.Play();
        (await first.WaitAsync(TestTimeout)).Should().BeTrue();
        await stop.CancelAsync();
        Func<Task> finish = () => DrainAsync(blocks).WaitAsync(TestTimeout);

        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [WindowsAudioFact]
    public async Task ReadAsync_WithSilentPlayback_CapturesSilenceAndCancels()
    {
        using MMDeviceEnumerator devices = new();
        using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        await using WasapiPlayer player = CreatePlayer(device, gain: 0);
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

        Task<bool> first = blocks.MoveNextAsync().AsTask();
        player.Play();
        (await first.WaitAsync(TestTimeout)).Should().BeTrue();
        blocks.Current.Samples.IsEmpty.Should().BeFalse();
        AudioProcessing.RmsDbfs(blocks.Current.Samples.Span).Should().BeLessThan(-90);
        await stop.CancelAsync();
        Func<Task> finish = () => DrainAsync(blocks).WaitAsync(TestTimeout);

        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [WindowsAudioFact]
    public async Task ReadAsync_WithoutPlayback_CancelsPendingRead()
    {
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();
        Task capture = DrainAsync(blocks);

        await stop.CancelAsync();
        Func<Task> finish = () => capture.WaitAsync(TestTimeout);

        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [WindowsAudioFact]
    public async Task DisposeAsync_WhileWaitingForAudio_CompletesCapture()
    {
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();
        Task capture = DrainAsync(blocks);

        await source.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await capture.WaitAsync(TestTimeout);

        capture.IsCompletedSuccessfully.Should().BeTrue();
    }

    [WindowsAudioFact]
    public async Task ReadAsync_AfterEarlyEnumerationExit_CanRestart()
    {
        using MMDeviceEnumerator devices = new();
        using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        await using WasapiPlayer player = CreatePlayer(device, gain: 0.1);
        await using WasapiLoopbackSource source = new(output.WriteLine);
        using CancellationTokenSource stop = new(TestTimeout);
        player.Play();

        for (int run = 0; run < 3; run++)
        {
            await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

            (await blocks.MoveNextAsync().AsTask().WaitAsync(TestTimeout)).Should().BeTrue();
            blocks.Current.Format.IsValid.Should().BeTrue();
            blocks.Current.Samples.IsEmpty.Should().BeFalse();
        }
    }

    private static WasapiPlayer CreatePlayer(MMDevice device, double gain)
    {
        WasapiPlayer player = new WasapiPlayerBuilder().WithDevice(device).Build();
        player.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = gain });
        return player;
    }

    private static async Task<(float[] Samples, AudioFormat Format)> ReadToneAsync(
        ISystemAudioSource source, CancellationToken cancellationToken)
    {
        List<float> samples = [];

        await foreach (AudioBlock block in source.ReadAsync(cancellationToken))
        {
            if (samples.Count == 0 && AudioProcessing.Peak(block.Samples.Span) < 0.005f)
            {
                continue;
            }

            float[] mono = new float[block.Samples.Length / block.Format.Channels];
            AudioProcessing.DownmixToMono(block.Samples.Span, block.Format.Channels, mono);
            samples.AddRange(mono);

            if (samples.Count >= block.Format.SampleRate)
            {
                return (samples.ToArray(), block.Format);
            }
        }

        throw new InvalidOperationException("Capture ended before a second of audio arrived.");
    }

    private static double EstimateFrequency(float[] samples, int sampleRate)
    {
        int crossings = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0)
            {
                crossings++;
            }
        }

        return crossings * (double)sampleRate / (samples.Length - 1);
    }

    private static async Task DrainAsync(IAsyncEnumerator<AudioBlock> blocks)
    {
        while (await blocks.MoveNextAsync())
        {
        }
    }
}
