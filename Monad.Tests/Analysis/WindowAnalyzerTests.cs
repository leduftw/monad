using System;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad;
using Monad.Analysis;
using Monad.Audio;
using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Analysis;

public sealed class WindowAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const int Rate = 8000;

    private static MonadConfig Config => new()
    {
        SilenceEnterDbfsThreshold = -65.0,
        NormalizePeakTarget = 0.90f,
        MaxConsecutiveFatalFailures = 3,
    };

    /// <summary>A square wave, so its RMS equals its amplitude exactly.</summary>
    private static float[] Tone(float amplitude, int samples = 800)
    {
        float[] window = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            window[i] = i % 2 == 0 ? amplitude : -amplitude;
        }

        return window;
    }

    private static Task<AnalysisStep> Analyze(WindowAnalyzer analyzer, float[] window) =>
        analyzer.AnalyzeAsync(window, Rate, T0, CancellationToken.None);

    // --- Levels are measured before anything touches the audio ---

    [Fact]
    public async Task AnalyzeAsync_ReportsTheTrueLevelOfQuietAudio()
    {
        // Arrange — amplitude 0.0002 is about -74 dBFS. Boosting first, as this
        // pipeline once did, would scale it to near full scale and report it as
        // roughly -1 dBFS, which is why silence was never detected.
        FakeRecognizer recognizer = new();
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.0002f));

        // Assert
        step.Dbfs.Should().BeApproximately(-74.0, 1.0);
        step.Dbfs.Should().BeLessThan(Config.SilenceEnterDbfsThreshold);
    }

    [Fact]
    public async Task AnalyzeAsync_WithSilentAudio_SpendsNoLookup()
    {
        // Arrange — every silent window used to cost an API call
        FakeRecognizer recognizer = new();
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, new float[800]);

        // Assert
        step.PerformedLookup.Should().BeFalse();
        recognizer.Calls.Should().Be(0);
        step.Sample!.IsNoMatch.Should().BeTrue();
        step.Sample.SongKey.Should().BeNull();
        step.Sample.TimestampUtc.Should().Be(T0);
    }

    [Fact]
    public async Task AnalyzeAsync_WithAudibleAudio_PerformsALookup()
    {
        // Arrange
        FakeRecognizer recognizer = new();
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.05f)); // about -26 dBFS

        // Assert
        step.PerformedLookup.Should().BeTrue();
        recognizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_SendsBoostedAudioWhileStillReportingTheRealLevel()
    {
        // Arrange — the service gets a healthy signal, the segment tracker gets
        // the truth. Both matter, which is why the order of operations does.
        FakeRecognizer recognizer = new();
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.02f)); // about -34 dBFS

        // Assert
        step.Dbfs.Should().BeApproximately(-34.0, 1.0);

        DecodedAudio sent = WavReader.ReadMono(new System.IO.MemoryStream(recognizer.Received[0]));
        AudioProcessing.Peak(sent.Mono).Should().BeApproximately(0.90f, 0.01f);
        sent.SampleRate.Should().Be(Rate);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotModifyTheCallersWindow()
    {
        // Arrange — the rolling buffer's window is reused every tick
        FakeRecognizer recognizer = new();
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);
        float[] window = Tone(0.02f);
        float[] original = (float[])window.Clone();

        // Act
        await Analyze(analyzer, window);

        // Assert
        window.Should().Equal(original);
    }

    // --- Turning outcomes into votes ---

    [Fact]
    public async Task AnalyzeAsync_WithAMatch_ProducesAKeyedSample()
    {
        // Arrange
        FakeRecognizer recognizer = new(FakeRecognizer.Match("Nina Simone", "Feeling Good", "USSM17300123"));
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.05f));

        // Assert
        step.Sample!.SongKey.Should().Be("isrc:USSM17300123");
        step.Sample.IsNoMatch.Should().BeFalse();
        step.Sample.DisplayText.Should().Be("Nina Simone - Feeling Good [ISRC: USSM17300123]");
    }

    [Fact]
    public async Task AnalyzeAsync_WithAMatchCarryingNothingIdentifiable_CountsAsNoMatch()
    {
        // Arrange — a result with no ISRC, artist or title yields no usable key
        FakeRecognizer recognizer = new(RecognitionOutcome.Matched(
            new RecognitionResult(string.Empty, string.Empty, IsrcInfo.None)));

        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.05f));

        // Assert
        step.Sample!.SongKey.Should().BeNull();
        step.Sample.IsNoMatch.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_WithNoMatch_ProducesAnUnkeyedSample()
    {
        // Arrange
        FakeRecognizer recognizer = new(RecognitionOutcome.NoMatch);
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.05f));

        // Assert
        step.Sample!.IsNoMatch.Should().BeTrue();
        step.Sample.SongKey.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_WithAFailedLookup_CastsNoVoteAtAll()
    {
        // Arrange — an outage is not evidence that the music stopped, so it
        // must not be allowed to end the segment that is playing.
        FakeRecognizer recognizer = new(RecognitionOutcome.Failed("service down", isTransient: true));
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        AnalysisStep step = await Analyze(analyzer, Tone(0.05f));

        // Assert
        step.Sample.Should().BeNull();
        step.PerformedLookup.Should().BeTrue();
    }

    // --- Giving up ---

    [Fact]
    public async Task AnalyzeAsync_AfterRepeatedUnrecoverableFailures_GivesUp()
    {
        // Arrange
        FakeRecognizer recognizer = new(RecognitionOutcome.Failed("bad token", isTransient: false));
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act
        await Analyze(analyzer, Tone(0.05f));
        await Analyze(analyzer, Tone(0.05f));

        // Assert — the third one is the last straw
        await FluentActions
            .Awaiting(() => Analyze(analyzer, Tone(0.05f)))
            .Should().ThrowAsync<MonadFatalException>()
            .WithMessage("*bad token*");
    }

    [Fact]
    public async Task AnalyzeAsync_KeepsGoingThroughRepeatedTransientFailures()
    {
        // Arrange
        FakeRecognizer recognizer = new(RecognitionOutcome.Failed("rate limited", isTransient: true));
        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act / Assert
        for (int i = 0; i < 10; i++)
        {
            (await Analyze(analyzer, Tone(0.05f))).Sample.Should().BeNull();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_AfterASuccess_ForgivesEarlierFailures()
    {
        // Arrange — two unrecoverable failures, then a match, then two more
        FakeRecognizer recognizer = new(
            RecognitionOutcome.Failed("bad token", isTransient: false),
            RecognitionOutcome.Failed("bad token", isTransient: false),
            FakeRecognizer.Match("A", "B", "XX1234567890"),
            RecognitionOutcome.Failed("bad token", isTransient: false),
            RecognitionOutcome.Failed("bad token", isTransient: false));

        WindowAnalyzer analyzer = new(recognizer, Config, MonadLog.Silent);

        // Act / Assert — the counter restarted, so five calls do not trip it
        for (int i = 0; i < 5; i++)
        {
            await Analyze(analyzer, Tone(0.05f));
        }
    }
}
