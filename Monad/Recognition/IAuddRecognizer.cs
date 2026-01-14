using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Recognition;

public interface IAuddRecognizer
{
    Task<JsonDocument> RecognizeAsync(byte[] wavBytes, CancellationToken ct);
}
