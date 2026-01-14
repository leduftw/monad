using System;
using System.Net.Http;
using System.Threading;

using Monad;

const int RecordSeconds = 12;
const int SleepBetweenSeconds = 4;

string? token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("AUDD_API_TOKEN not set. Example: setx AUDD_API_TOKEN \"<token>\"");
    return;
}

using HttpClient http = new()
{
    Timeout = TimeSpan.FromSeconds(30)
};

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

MonadApp app = new(
    httpClient: http,
    auddToken: token,
    recordSeconds: RecordSeconds,
    sleepBetweenSeconds: SleepBetweenSeconds);

await app.RunAsync(cts.Token);
