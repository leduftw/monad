using System;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Audio;

public interface IAudioRecorder
{
    Task<RecordedAudio> RecordAsync(TimeSpan duration, CancellationToken ct);
}
