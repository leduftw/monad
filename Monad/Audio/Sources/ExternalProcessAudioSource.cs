using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Audio.Sources;

/// <summary>
/// Streams audio from a helper process that writes raw little-endian float32
/// PCM to stdout. Used for the macOS tap sidecar and for the PulseAudio monitor
/// capture on Linux.
/// </summary>
/// <remarks>
/// The helper's stdin is redirected and never written to. The macOS sidecar
/// treats EOF on that pipe as a graceful shutdown request; helpers that do not
/// watch stdin are killed after a short grace period. If the helper stops on its
/// own -- the macOS sidecar does this when the output device changes in a way it
/// cannot recover from -- it is restarted with a bounded backoff rather than
/// taking the whole session down.
/// </remarks>
public sealed class ExternalProcessAudioSource(
    string description,
    Func<ProcessStartInfo> startInfoFactory,
    AudioFormat? declaredFormat,
    Action<string> logDiagnostic) : ISystemAudioSource
{
    private const int ReadBufferBytes = 16 * 1024;
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(10);

    private Session? session;

    public string Description => description;

    public async IAsyncEnumerable<AudioBlock> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The float32 capture stream assumes a little-endian host.");
        }

        int failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            Session? current = await this.TryStartSessionAsync(cancellationToken).ConfigureAwait(false);

            if (current is null)
            {
                failures++;
            }
            else
            {
                await using (current)
                {
                    this.session = current;
                    failures = 0;

                    // The token has to be checked here as well as on the read:
                    // while audio is flowing the read always returns data and
                    // never observes cancellation, so without this the loop
                    // would spin forever and shutdown would hang.
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int samples = await current.ReadSamplesAsync(cancellationToken).ConfigureAwait(false);

                        if (samples <= 0)
                        {
                            break;
                        }

                        yield return new AudioBlock(current.Samples.AsMemory(0, samples), current.Format);
                    }
                }

                this.session = null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            failures = Math.Max(failures, 1);
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(MaxRestartDelay.TotalSeconds, Math.Pow(2, failures - 1)));
            logDiagnostic($"capture helper stopped; restarting in {delay.TotalSeconds:F0}s");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<Session?> TryStartSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Session.StartAsync(startInfoFactory(), declaredFormat, logDiagnostic, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logDiagnostic($"could not start capture helper: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session? current = this.session;
        this.session = null;

        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);

        private readonly Process process;
        private readonly Stream stdout;
        private readonly Action<string> log;
        private readonly byte[] bytes = new byte[ReadBufferBytes];
        private readonly object terminationGate = new();

        private CancellationTokenRegistration shutdown;
        private Task? terminationTask;

        private Session(Process process, Stream stdout, AudioFormat format, Action<string> log)
        {
            this.process = process;
            this.stdout = stdout;
            this.Format = format;
            this.log = log;
            this.Samples = new float[ReadBufferBytes / sizeof(float)];
        }

        /// <summary>
        /// Starts helper shutdown. Closing stdin gives the macOS sidecar a chance
        /// to clean up its tap, then a bounded fallback kills helpers such as
        /// parec that neither watch stdin nor produce more output. The fallback
        /// is needed because a pending pipe read does not reliably observe
        /// cancellation on every Unix platform.
        /// </summary>
        private void RequestHelperExit()
        {
            _ = this.TerminateAsync();
        }

        public AudioFormat Format { get; }

        public float[] Samples { get; }

        public static async Task<Session> StartAsync(
            ProcessStartInfo startInfo,
            AudioFormat? declaredFormat,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            startInfo.UseShellExecute = false;

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"'{startInfo.FileName}' did not start.");

            PumpDiagnostics(process, log);

            // StandardOutput is a text reader; the audio must come off the raw stream.
            Stream stdout = process.StandardOutput.BaseStream;

            AudioFormat format;

            try
            {
                format = declaredFormat ?? await ReadHeaderAsync(stdout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await TerminateAsync(process).ConfigureAwait(false);
                throw;
            }

            if (!format.IsValid)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                throw new InvalidDataException($"Capture helper reported an unusable format ({format}).");
            }

            Session session = new(process, stdout, format, log);
            session.shutdown = cancellationToken.Register(session.RequestHelperExit);

            return session;
        }

        /// <summary>Reads the JSON header line the helper emits before any audio.</summary>
        private static async Task<AudioFormat> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
        {
            using MemoryStream line = new();
            byte[] one = new byte[1];

            while (line.Length < 4096)
            {
                int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    throw new EndOfStreamException("Capture helper exited before announcing its audio format.");
                }

                if (one[0] == (byte)'\n')
                {
                    using JsonDocument document = JsonDocument.Parse(line.ToArray());
                    JsonElement root = document.RootElement;

                    int sampleRate = root.TryGetProperty("sampleRate", out JsonElement rate) && rate.ValueKind == JsonValueKind.Number
                        ? (int)rate.GetDouble()
                        : 0;

                    int channels = root.TryGetProperty("channels", out JsonElement ch) && ch.ValueKind == JsonValueKind.Number
                        ? ch.GetInt32()
                        : 0;

                    string? encoding = root.TryGetProperty("format", out JsonElement fmt) && fmt.ValueKind == JsonValueKind.String
                        ? fmt.GetString()
                        : null;

                    if (encoding is not null && !string.Equals(encoding, "f32le", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Capture helper offered '{encoding}'; only 'f32le' is supported.");
                    }

                    return new AudioFormat(sampleRate, channels);
                }

                line.WriteByte(one[0]);
            }

            throw new InvalidDataException("Capture helper sent an oversized header line.");
        }

        /// <summary>Reads the next block, returning the number of samples, or 0 at end of stream.</summary>
        public async Task<int> ReadSamplesAsync(CancellationToken cancellationToken)
        {
            int offset = 0;

            try
            {
                // Only whole samples can be converted, so keep any trailing
                // bytes of a split sample for the next read.
                int read = await this.stdout.ReadAsync(this.bytes.AsMemory(), cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    return 0;
                }

                offset = read;

                while (offset % sizeof(float) != 0)
                {
                    int extra = await this.stdout
                        .ReadAsync(this.bytes.AsMemory(offset, sizeof(float) - (offset % sizeof(float))), cancellationToken)
                        .ConfigureAwait(false);

                    if (extra <= 0)
                    {
                        break;
                    }

                    offset += extra;
                }
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                this.log($"capture stream ended: {ex.Message}");
                return 0;
            }

            int samples = offset / sizeof(float);
            ReadOnlySpan<byte> span = this.bytes.AsSpan(0, samples * sizeof(float));

            MemoryMarshal.Cast<byte, float>(span).CopyTo(this.Samples);

            return samples;
        }

        private static void PumpDiagnostics(Process process, Action<string> log)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            log(line);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The helper is gone; nothing left to report.
                }
            });
        }

        private static async Task TerminateAsync(Process process)
        {
            try
            {
                // Closing stdin is the helper's cue to shut down cleanly.
                if (!process.HasExited)
                {
                    process.StandardInput.Close();

                    using CancellationTokenSource timeout = new(GracefulShutdownTimeout);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
            {
                // Fall through to the hard kill.
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already gone.
            }

            process.Dispose();
        }

        /// <summary>
        /// Starts process termination exactly once so cancellation and disposal
        /// can race without closing or disposing the same process independently.
        /// </summary>
        private Task TerminateAsync()
        {
            lock (this.terminationGate)
            {
                return this.terminationTask ??= TerminateAsync(this.process);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this.shutdown.DisposeAsync().ConfigureAwait(false);
            await this.TerminateAsync().ConfigureAwait(false);
        }
    }
}
