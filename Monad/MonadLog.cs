using System;

namespace Monad;

/// <summary>
/// Where the running commentary goes. Segments are not written through here --
/// they go to an <see cref="Output.ISegmentSink"/>, so that redirecting the
/// log never costs you the actual results.
/// </summary>
public sealed class MonadLog(Action<string>? status, Action<string>? diagnostic)
{
    public static MonadLog Silent { get; } = new(null, null);

    public static MonadLog ForConsole(bool verbose, bool quiet) =>
        new(
            status: quiet ? null : Console.WriteLine,
            diagnostic: verbose ? message => Console.WriteLine($"  · {message}") : null);

    public void Status(string message) => status?.Invoke(message);

    public void Diagnostic(string message) => diagnostic?.Invoke(message);
}

/// <summary>Raised when the run cannot usefully continue, such as a rejected API token.</summary>
public sealed class MonadFatalException(string message) : Exception(message);
