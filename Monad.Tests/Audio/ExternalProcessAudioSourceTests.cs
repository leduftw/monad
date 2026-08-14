using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Audio;
using Monad.Audio.Sources;

using Xunit;

namespace Monad.Tests.Audio;

public sealed class ExternalProcessAudioSourceTests
{
    private const string StartedPrefix = "helper-pid:";
    private const string GracefulExitMessage = "graceful-exit";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadAsync_WhenSilentHelperIgnoresStdin_CancellationTerminatesHelper()
    {
        // Arrange
        HelperObserver observer = new();
        await using ExternalProcessAudioSource source = CreateSource(readsStdin: false, observer);
        using CancellationTokenSource stop = new();
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

        Task<bool> pendingRead = blocks.MoveNextAsync().AsTask();
        int processId = await observer.Started.Task.WaitAsync(TestTimeout);
        using Process helper = Process.GetProcessById(processId);

        // Act
        await stop.CancelAsync();
        bool producedBlock = await pendingRead.WaitAsync(TestTimeout);
        await helper.WaitForExitAsync().WaitAsync(TestTimeout);

        // Assert
        producedBlock.Should().BeFalse();
        helper.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenHelperWatchesStdin_CancellationAllowsGracefulExit()
    {
        // Arrange
        HelperObserver observer = new();
        await using ExternalProcessAudioSource source = CreateSource(readsStdin: true, observer);
        using CancellationTokenSource stop = new();
        await using IAsyncEnumerator<AudioBlock> blocks = source.ReadAsync(stop.Token).GetAsyncEnumerator();

        Task<bool> pendingRead = blocks.MoveNextAsync().AsTask();
        int processId = await observer.Started.Task.WaitAsync(TestTimeout);
        using Process helper = Process.GetProcessById(processId);

        // Act
        await stop.CancelAsync();
        bool producedBlock = await pendingRead.WaitAsync(TestTimeout);
        string exitMessage = await observer.GracefulExit.Task.WaitAsync(TestTimeout);
        await helper.WaitForExitAsync().WaitAsync(TestTimeout);

        // Assert
        producedBlock.Should().BeFalse();
        exitMessage.Should().Be(GracefulExitMessage);
        helper.HasExited.Should().BeTrue();
    }

    private static ExternalProcessAudioSource CreateSource(bool readsStdin, HelperObserver observer) =>
        new(
            description: "silent test helper",
            startInfoFactory: () => CreateHelperStartInfo(readsStdin),
            declaredFormat: new AudioFormat(48000, 2),
            logDiagnostic: observer.Observe);

    private static ProcessStartInfo CreateHelperStartInfo(bool readsStdin)
    {
        if (OperatingSystem.IsWindows())
        {
            string script = readsStdin
                ? $"[Console]::Error.WriteLine('{StartedPrefix}' + $PID); "
                    + "$null = [Console]::In.ReadLine(); "
                    + $"[Console]::Error.WriteLine('{GracefulExitMessage}')"
                : $"[Console]::Error.WriteLine('{StartedPrefix}' + $PID); "
                    + "while ($true) { Start-Sleep -Seconds 60 }";

            ProcessStartInfo windows = new("powershell.exe");
            windows.ArgumentList.Add("-NoLogo");
            windows.ArgumentList.Add("-NoProfile");
            windows.ArgumentList.Add("-NonInteractive");
            windows.ArgumentList.Add("-Command");
            windows.ArgumentList.Add(script);
            return windows;
        }

        string command = readsStdin
            ? $"echo {StartedPrefix}$$ >&2; IFS= read -r ignored || :; echo {GracefulExitMessage} >&2"
            : $"echo {StartedPrefix}$$ >&2; while :; do sleep 60; done";

        ProcessStartInfo unix = new("/bin/sh");
        unix.ArgumentList.Add("-c");
        unix.ArgumentList.Add(command);
        return unix;
    }

    private sealed class HelperObserver
    {
        public TaskCompletionSource<int> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> GracefulExit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Observe(string message)
        {
            if (message.StartsWith(StartedPrefix, StringComparison.Ordinal)
                && int.TryParse(
                    message.AsSpan(StartedPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int processId))
            {
                this.Started.TrySetResult(processId);
            }

            if (string.Equals(message, GracefulExitMessage, StringComparison.Ordinal))
            {
                this.GracefulExit.TrySetResult(message);
            }
        }
    }
}
