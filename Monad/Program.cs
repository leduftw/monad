using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Monad;
using Monad.Analysis;
using Monad.Audio;
using Monad.Audio.Sources;
using Monad.Cli;
using Monad.Output;
using Monad.Recognition;

ParseResult parsed = CommandLineParser.Parse(args);

if (parsed.Error is { } parseError)
{
    Console.Error.WriteLine($"monad: {parseError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLineParser.Usage);
    return 64; // EX_USAGE
}

MonadOptions options = parsed.Options!;

switch (options.Command)
{
    case MonadCommand.Help:
        Console.WriteLine(CommandLineParser.Usage);
        return 0;

    case MonadCommand.Version:
        Console.WriteLine($"monad {Version()}");
        return 0;
}

MonadLog log = MonadLog.ForConsole(options.Verbose, options.Quiet);

using CancellationTokenSource cancellation = new();

// Signals are handled through PosixSignalRegistration rather than
// Console.CancelKeyPress. It covers SIGTERM as well as SIGINT -- which matters
// for something meant to run unattended, where `kill`, launchd and systemd all
// send SIGTERM -- and it is delivered reliably whether or not the process owns
// a terminal, which Console.CancelKeyPress is not.
List<PosixSignalRegistration> signals = [];

foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
{
    try
    {
        signals.Add(PosixSignalRegistration.Create(signal, context =>
        {
            // A second signal means the caller is not waiting: let the default
            // action run and take the process down now.
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            context.Cancel = true; // wind down and report the last segment instead of dying here
            cancellation.Cancel();
        }));
    }
    catch (PlatformNotSupportedException)
    {
        // Not every signal exists on every platform; the others still apply.
    }
}

try
{
    return options.Command switch
    {
        MonadCommand.Devices => await ShowDevicesAsync(options, log).ConfigureAwait(false),
        MonadCommand.Replay => await ReplayAsync(options, log, cancellation.Token).ConfigureAwait(false),
        _ => await MonitorAsync(options, log, cancellation.Token).ConfigureAwait(false),
    };
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex) when (ex is MonadFatalException
    or PlatformNotSupportedException
    or FileNotFoundException
    or InvalidDataException
    or ArgumentException)
{
    Console.Error.WriteLine($"monad: {ex.Message}");
    return 1;
}

async Task<int> MonitorAsync(MonadOptions opts, MonadLog logger, CancellationToken cancellationToken)
{
    if (!opts.NoRecognize && string.IsNullOrWhiteSpace(opts.Token))
    {
        Console.Error.WriteLine(
            $"monad: no API token. Set {CommandLineParser.TokenVariable}, pass --token, put it in a config "
            + "file, or run with --no-recognize to capture without identifying anything.");

        return 1;
    }

    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    ISongRecognizer recognizer = opts.NoRecognize
        ? new NullRecognizer()
        : new AuddRecognizer(http, opts.Token!, opts.Config.MaxRecognitionAttempts, logger.Diagnostic);

    await using ISystemAudioSource source = AudioSourceFactory.Create(opts.Source, logger.Diagnostic);
    await using ISegmentSink sink = BuildSink(opts, logger);

    if (opts.NoRecognize)
    {
        logger.Status("Running with --no-recognize: capture and levels only, nothing will be identified.");
    }

    MonadApp app = new(source, recognizer, sink, opts.Config, TimeProvider.System, logger);
    RunSummary summary = await app.RunAsync(cancellationToken).ConfigureAwait(false);

    Report(summary, logger);

    return 0;
}

async Task<int> ReplayAsync(MonadOptions opts, MonadLog logger, CancellationToken cancellationToken)
{
    if (!File.Exists(opts.InputPath))
    {
        Console.Error.WriteLine($"monad: '{opts.InputPath}' does not exist.");
        return 1;
    }

    if (!opts.NoRecognize && string.IsNullOrWhiteSpace(opts.Token))
    {
        Console.Error.WriteLine(
            $"monad: replay needs an API token ({CommandLineParser.TokenVariable} or --token), "
            + "or --no-recognize to exercise the pipeline without it.");

        return 1;
    }

    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    ISongRecognizer recognizer = opts.NoRecognize
        ? new NullRecognizer()
        : new AuddRecognizer(http, opts.Token!, opts.Config.MaxRecognitionAttempts, logger.Diagnostic);

    DecodedAudio audio = WavReader.ReadMono(opts.InputPath!);

    await using ISegmentSink sink = BuildSink(opts, logger);

    RunSummary summary = await ReplayRunner
        .RunAsync(audio, recognizer, sink, opts.Config, DateTimeOffset.UtcNow, logger, cancellationToken)
        .ConfigureAwait(false);

    Report(summary, logger);

    return 0;
}

async Task<int> ShowDevicesAsync(MonadOptions opts, MonadLog logger)
{
    AudioSourceKind kind = AudioSourceFactory.Resolve(opts.Source);
    Console.WriteLine($"Capture path: {kind}");

    await using ISystemAudioSource source = AudioSourceFactory.Create(opts.Source, logger.Diagnostic);
    Console.WriteLine($"             {source.Description}");

    if (kind != AudioSourceKind.MacTap)
    {
        return 0;
    }

    // The sidecar owns the Core Audio device list, so ask it.
    string sidecar = AudioSourceFactory.LocateSidecar();
    Console.WriteLine();
    Console.WriteLine("Output devices:");

    ProcessStartInfo info = new(sidecar) { RedirectStandardOutput = true, UseShellExecute = false };
    info.ArgumentList.Add("devices");

    using Process? process = Process.Start(info);

    if (process is null)
    {
        Console.Error.WriteLine($"monad: could not run '{sidecar}'.");
        return 1;
    }

    Console.WriteLine(await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false));
    await process.WaitForExitAsync().ConfigureAwait(false);

    return process.ExitCode;
}

static ISegmentSink BuildSink(MonadOptions opts, MonadLog logger)
{
    List<ISegmentSink> sinks = [new ConsoleSegmentSink()];

    if (!string.IsNullOrWhiteSpace(opts.JsonlPath))
    {
        JsonlSegmentSink jsonl = new(opts.JsonlPath);
        logger.Status($"Appending segments to {jsonl.FilePath}");
        sinks.Add(jsonl);
    }

    return sinks.Count == 1 ? sinks[0] : new CompositeSegmentSink(sinks);
}

static void Report(RunSummary summary, MonadLog logger) =>
    logger.Status(
        $"\nStopped. {summary.WindowsAnalyzed} windows analysed, "
        + $"{summary.Lookups} lookups, {summary.SegmentsEmitted} segments.");

static string Version()
{
    Assembly assembly = Assembly.GetExecutingAssembly();

    return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
