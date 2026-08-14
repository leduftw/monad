using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad;
using Monad.Aggregation;
using Monad.Audio;
using Monad.Output;
using Monad.Recognition;

using Xunit;

namespace Monad.Tests;

/// <summary>
/// Wiring-level coverage of the live loop: continuous capture feeding a rolling
/// buffer, a timer slicing windows off it, and a clean stop. Runs on the real
/// clock with one-second windows, so these are seconds-long, not milliseconds.
/// </summary>
public sealed class MonadAppTests
{
    private const int Rate = 8000;

    private static MonadConfig Config => new()
    {
        WindowSeconds = 1,
        IntervalSeconds = 1,
        BufferSeconds = 5,
        PreferredSampleRate = Rate,
        RingBufferMaxSamples = 1,
        RingBufferMaxAgeSeconds = 120,
        VoteWeightsNewestToOldest = [1.0],
        MinLeaderShare = 0.50,
        LeaderPersistLoops = 1,
        MinSegmentDurationSeconds = 0,
        SilenceEnterPersistLoops = 2,
        UnknownPersistLoops = 2,
    };

    [Fact]
    public async Task RunAsync_CapturesAudioIdentifiesItAndReportsASegment()
    {
        // Arrange
        FakeAudioSource source = FakeAudioSource.Tone(
            amplitude: 0.2f, sampleRate: Rate, channels: 2, cadence: TimeSpan.FromMilliseconds(100));

        FakeRecognizer recognizer = new(FakeRecognizer.Match("Nina Simone", "Feeling Good", "USSM17300123"));
        RecordingSegmentSink sink = new();

        MonadApp app = new(source, recognizer, sink, Config, TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(4));

        // Act
        RunSummary summary = await app.RunAsync(stop.Token);

        // Assert
        summary.WindowsAnalyzed.Should().BeGreaterThan(0);
        summary.Lookups.Should().BeGreaterThan(0);

        sink.Segments.Should().ContainSingle();
        sink.Segments[0].Kind.Should().Be(SegmentKind.Song);
        sink.Segments[0].SongKey.Should().Be("isrc:USSM17300123");
    }

    [Fact]
    public async Task RunAsync_WithNoAudioArriving_ReportsSilenceAndSpendsNoLookups()
    {
        // Arrange — a source that never yields, which is exactly what macOS
        // looks like when nothing is playing.
        SilentSource source = new();
        FakeRecognizer recognizer = new();
        RecordingSegmentSink sink = new();

        MonadApp app = new(source, recognizer, sink, Config, TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(4));

        // Act
        RunSummary summary = await app.RunAsync(stop.Token);

        // Assert
        summary.WindowsAnalyzed.Should().BeGreaterThan(0);
        summary.Lookups.Should().Be(0);
        recognizer.Calls.Should().Be(0);

        sink.Segments.Should().AllSatisfy(segment => segment.Kind.Should().Be(SegmentKind.Silence));
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsPromptlyWhileAudioIsStillFlowing()
    {
        // Arrange — the capture loop has to notice cancellation even though its
        // reads keep succeeding; otherwise shutdown hangs waiting for it.
        FakeAudioSource source = FakeAudioSource.Tone(
            amplitude: 0.2f, sampleRate: Rate, channels: 2, cadence: TimeSpan.FromMilliseconds(50));

        MonadApp app = new(source, new FakeRecognizer(), new RecordingSegmentSink(), Config,
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));

        // Act
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await app.RunAsync(stop.Token);
        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;

        // Assert
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_WhenCaptureSourceEndsUnexpectedly_ThrowsFatalError()
    {
        // Arrange
        MonadApp app = new(new EndingSource(), new FakeRecognizer(), new RecordingSegmentSink(), Config,
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

        // Act / Assert
        await FluentActions
            .Awaiting(() => app.RunAsync(timeout.Token))
            .Should().ThrowAsync<MonadFatalException>()
            .WithMessage("*stopped unexpectedly*");
    }

    [Fact]
    public async Task RunAsync_WhenCaptureSourceThrows_ThrowsFatalErrorWithCause()
    {
        // Arrange
        MonadApp app = new(new FailingSource(), new FakeRecognizer(), new RecordingSegmentSink(), Config,
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

        // Act / Assert
        await FluentActions
            .Awaiting(() => app.RunAsync(timeout.Token))
            .Should().ThrowAsync<MonadFatalException>()
            .WithMessage("*device vanished*");
    }

    [Fact]
    public async Task RunAsync_WhenCaptureFailsWhileSinkObservesCancellation_PreservesCaptureFailure()
    {
        // Arrange — the first recognition starts a song and the second ends it,
        // putting analysis inside the sink. Capture then fails, and the sink
        // observes the linked run cancellation while that failure is drained.
        CancellationAwareSink sink = new();
        FailAfterSignalSource source = new(sink.Entered);
        FakeRecognizer recognizer = new(
            FakeRecognizer.Match("Nina Simone", "Feeling Good", "USSM17300123"),
            RecognitionOutcome.NoMatch);

        MonadApp app = new(source, recognizer, sink, Config with { UnknownPersistLoops = 1 },
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        // Act / Assert
        await FluentActions
            .Awaiting(() => app.RunAsync(timeout.Token))
            .Should().ThrowAsync<MonadFatalException>()
            .WithMessage("*device vanished*");

        sink.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WhenCaptureThrowsDuringCallerCancellation_StopsGracefully()
    {
        // Arrange — platform capture APIs sometimes report a transport error as
        // their cancellation path is tearing them down. It must not turn an
        // ordinary Ctrl+C into a fatal exit.
        MonadApp app = new(new ShutdownFailingSource(), new FakeRecognizer(), new RecordingSegmentSink(), Config,
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(100));

        // Act
        RunSummary summary = await app.RunAsync(stop.Token);

        // Assert
        summary.WindowsAnalyzed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AdoptsTheSampleRateTheSourceActuallyReports()
    {
        // Arrange — the configured rate is only a guess until audio arrives
        FakeAudioSource source = FakeAudioSource.Tone(
            amplitude: 0.2f, sampleRate: 44100, channels: 1, cadence: TimeSpan.FromMilliseconds(100));

        FakeRecognizer recognizer = new(FakeRecognizer.Match("A", "B", "AAA111111111"));
        RecordingSegmentSink sink = new();

        MonadApp app = new(source, recognizer, sink, Config with { PreferredSampleRate = 8000 },
            TimeProvider.System, MonadLog.Silent);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(4));

        // Act
        await app.RunAsync(stop.Token);

        // Assert — the encoded clip must carry the real rate, not the guess
        DecodedAudio sent = WavReader.ReadMono(new System.IO.MemoryStream(recognizer.Received[^1]));
        sent.SampleRate.Should().Be(44100);
    }

    [Fact]
    public async Task RunAsync_WithAnInvalidConfiguration_Throws()
    {
        // Arrange
        MonadConfig broken = Config with { BufferSeconds = 0 };

        MonadApp app = new(new SilentSource(), new FakeRecognizer(), new RecordingSegmentSink(), broken,
            TimeProvider.System, MonadLog.Silent);

        // Act / Assert
        await FluentActions
            .Awaiting(() => app.RunAsync(CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>Never yields a block, mirroring a machine that is playing nothing.</summary>
    private sealed class SilentSource : ISystemAudioSource
    {
        public string Description => "silent source";

        public async System.Collections.Generic.IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A source that reaches EOF even though the run did not ask it to stop.</summary>
    private sealed class EndingSource : ISystemAudioSource
    {
        public string Description => "ending source";

        public async System.Collections.Generic.IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A source whose capture backend fails while the run is active.</summary>
    private sealed class FailingSource : ISystemAudioSource
    {
        public string Description => "failing source";

        public async System.Collections.Generic.IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("device vanished");
            }

            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A source that reports a backend error only as cancellation tears it down.</summary>
    private sealed class ShutdownFailingSource : ISystemAudioSource
    {
        public string Description => "shutdown-failing source";

        public async System.Collections.Generic.IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("capture backend failed during shutdown");
            }

            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Keeps capture alive until analysis enters the sink, then fails.</summary>
    private sealed class FailAfterSignalSource(Task signal) : ISystemAudioSource
    {
        private static readonly float[] Samples = [.. Enumerable.Repeat(0.2f, Rate / 100)];

        public string Description => "coordinated failing source";

        public async System.Collections.Generic.IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!signal.IsCompleted)
            {
                yield return new AudioBlock(Samples, new AudioFormat(Rate, 1));
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("device vanished while emitting");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink whose pending write ends by observing run cancellation.</summary>
    private sealed class CancellationAwareSink : ISegmentSink
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => this.entered.Task;

        public bool CancellationObserved { get; private set; }

        public async ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken)
        {
            this.entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.CancellationObserved = true;
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
