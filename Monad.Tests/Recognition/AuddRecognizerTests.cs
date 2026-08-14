using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Recognition;

using Xunit;

namespace Monad.Tests.Recognition;

public sealed class AuddRecognizerTests
{
    private static readonly byte[] Wav = [0x52, 0x49, 0x46, 0x46];

    // --- Response parsing ---

    private static RecognitionOutcome Parse(string json) =>
        AuddRecognizer.Parse(JsonDocument.Parse(json));

    [Fact]
    public void Parse_WithAMatch_ReturnsTheTrack()
    {
        // Arrange / Act
        RecognitionOutcome outcome = Parse(
            """{"status":"success","result":{"artist":"Nina Simone","title":"Feeling Good","isrc":"USSM17300123"}}""");

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Match);
        outcome.Result!.Artist.Should().Be("Nina Simone");
        outcome.Result.IsrcInfo.Selected.Should().Be("USSM17300123");
    }

    [Theory]
    [InlineData("""{"status":"success","result":null}""")]
    [InlineData("""{"status":"success"}""")]
    [InlineData("""{"status":"success","result":[]}""")]
    public void Parse_WithNoResult_ReturnsNoMatch(string json)
    {
        Parse(json).Status.Should().Be(RecognitionStatus.NoMatch);
    }

    [Theory]
    [InlineData(900)] // authentication failed
    [InlineData(901)] // no token, or the quota is spent
    public void Parse_WithAnAccountError_ReportsAnUnrecoverableFailure(int code)
    {
        // Arrange / Act — retrying these forever would just burn the loop
        RecognitionOutcome outcome = Parse(
            $$$"""{"status":"error","error":{"error_code":{{{code}}},"error_message":"nope"}}""");

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain(code.ToString()).And.Contain("nope");
    }

    [Fact]
    public void Parse_WithAFingerprintingError_ReportsATransientFailure()
    {
        // Arrange / Act
        RecognitionOutcome outcome = Parse(
            """{"status":"error","error":{"error_code":300,"error_message":"fingerprinting failed"}}""");

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"status":"error"}""")]
    [InlineData("""{"status":"error","error":"broken"}""")]
    [InlineData("""{"status":"weird"}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"hello\"")]
    public void Parse_WithAnUnrecognisableEnvelope_ReportsATransientFailure(string json)
    {
        // Arrange / Act
        RecognitionOutcome outcome = Parse(json);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithAMistypedErrorCode_StillReportsTheMessage()
    {
        // Arrange / Act
        RecognitionOutcome outcome = Parse(
            """{"status":"error","error":{"error_code":"901","error_message":"quota"}}""");

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.ErrorMessage.Should().Contain("quota");
    }

    // --- HTTP behaviour ---

    [Fact]
    public async Task RecognizeAsync_SendsTheTokenAndTheAudio()
    {
        // Arrange
        StubHandler handler = new(_ => Json("""{"status":"success","result":null}"""));
        AuddRecognizer recognizer = new(new HttpClient(handler), "secret-token", maxAttempts: 1);

        // Act
        await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        handler.Requests.Should().ContainSingle();
        handler.Bodies[0].Should().Contain("secret-token").And.Contain("api_token").And.Contain("snippet.wav");
        handler.Requests[0].RequestUri!.ToString().Should().Be("https://api.audd.io/");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task RecognizeAsync_ClassifiesHttpFailures(HttpStatusCode status, bool expectedTransient)
    {
        // Arrange
        StubHandler handler = new(_ => new HttpResponseMessage(status));
        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 1);

        // Act
        RecognitionOutcome outcome = await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().Be(expectedTransient);
        outcome.ErrorMessage.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task RecognizeAsync_RetriesATransientFailureAndSucceeds()
    {
        // Arrange
        StubHandler handler = new(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("""{"status":"success","result":{"artist":"A","title":"B","isrc":"XX1234567890"}}"""));

        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 2);

        // Act
        RecognitionOutcome outcome = await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Match);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecognizeAsync_DoesNotRetryAnUnrecoverableFailure()
    {
        // Arrange — a rejected token will be rejected again a second later
        StubHandler handler = new(_ => Json(
            """{"status":"error","error":{"error_code":900,"error_message":"bad token"}}"""));

        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 3);

        // Act
        RecognitionOutcome outcome = await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.IsTransient.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RecognizeAsync_DoesNotRetryAMatch()
    {
        // Arrange
        StubHandler handler = new(_ => Json("""{"status":"success","result":null}"""));
        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 3);

        // Act
        await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RecognizeAsync_WithMalformedJson_ReportsATransientFailure()
    {
        // Arrange
        StubHandler handler = new(_ => Json("this is not json"));
        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 1);

        // Act
        RecognitionOutcome outcome = await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task RecognizeAsync_WithANetworkError_ReportsATransientFailure()
    {
        // Arrange
        StubHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 1);

        // Act
        RecognitionOutcome outcome = await recognizer.RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.Failed);
        outcome.IsTransient.Should().BeTrue();
        outcome.ErrorMessage.Should().Contain("connection refused");
    }

    [Fact]
    public async Task RecognizeAsync_WhenCancelled_Propagates()
    {
        // Arrange
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        StubHandler handler = new(_ => Json("""{"status":"success","result":null}"""));
        AuddRecognizer recognizer = new(new HttpClient(handler), "token", maxAttempts: 1);

        // Act / Assert
        await FluentActions
            .Awaiting(() => recognizer.RecognizeAsync(Wav, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NullRecognizer_AlwaysReportsNoMatch()
    {
        // Arrange / Act
        RecognitionOutcome outcome = await new NullRecognizer().RecognizeAsync(Wav, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(RecognitionStatus.NoMatch);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            this.Requests.Add(request);
            this.Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return respond(this.Requests.Count);
        }
    }
}
