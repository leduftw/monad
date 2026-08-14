using System;
using System.Collections.Generic;
using System.Threading;

namespace Monad.Audio;

/// <summary>A block of interleaved samples handed over by an audio source.</summary>
/// <remarks>
/// <see cref="Samples"/> is only guaranteed to be valid until the enumerator
/// advances -- sources are free to reuse their buffer. Copy anything you need
/// to keep. <see cref="Format"/> travels with each block because a source may
/// renegotiate it mid-stream, for instance after the user switches output
/// devices.
/// </remarks>
public readonly record struct AudioBlock(ReadOnlyMemory<float> Samples, AudioFormat Format);

/// <summary>
/// A continuously running capture of whatever the machine is playing.
/// Implementations exist per platform; see <c>Audio/Sources</c>.
/// </summary>
public interface ISystemAudioSource : IAsyncDisposable
{
    /// <summary>Short description of the capture path, shown at startup.</summary>
    string Description { get; }

    /// <summary>Streams audio until cancelled or until the source ends.</summary>
    IAsyncEnumerable<AudioBlock> ReadAsync(CancellationToken cancellationToken);
}
