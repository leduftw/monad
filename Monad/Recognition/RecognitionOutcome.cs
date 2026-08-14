using System;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Recognition;

public enum RecognitionStatus
{
    /// <summary>A track was identified.</summary>
    Match = 0,

    /// <summary>The service answered, but recognised nothing.</summary>
    NoMatch = 1,

    /// <summary>The lookup did not complete.</summary>
    Failed = 2,
}

/// <summary>The result of one recognition attempt.</summary>
/// <param name="IsTransient">
/// Whether retrying later is worthwhile. Rate limits and server errors are
/// transient; a rejected API token is not, and repeated non-transient failures
/// stop the run rather than quietly burning through the loop forever.
/// </param>
public sealed record class RecognitionOutcome(
    RecognitionStatus Status,
    RecognitionResult? Result = null,
    string? ErrorMessage = null,
    bool IsTransient = false)
{
    public static RecognitionOutcome NoMatch { get; } = new(RecognitionStatus.NoMatch);

    public static RecognitionOutcome Matched(RecognitionResult result) =>
        new(RecognitionStatus.Match, result);

    public static RecognitionOutcome Failed(string message, bool isTransient) =>
        new(RecognitionStatus.Failed, ErrorMessage: message, IsTransient: isTransient);
}

/// <summary>Identifies the track in a short audio clip.</summary>
public interface ISongRecognizer
{
    Task<RecognitionOutcome> RecognizeAsync(byte[] wavBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Stands in for a real service when <c>--no-recognize</c> is set, so capture,
/// levels and segmentation can be exercised without spending API quota.
/// </summary>
public sealed class NullRecognizer : ISongRecognizer
{
    public Task<RecognitionOutcome> RecognizeAsync(byte[] wavBytes, CancellationToken cancellationToken) =>
        Task.FromResult(RecognitionOutcome.NoMatch);
}
