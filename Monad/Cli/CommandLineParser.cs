using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Monad.Audio.Sources;

namespace Monad.Cli;

public sealed record class ParseResult(MonadOptions? Options, string? Error)
{
    public static ParseResult Failed(string error) => new(null, error);

    public static ParseResult Ok(MonadOptions options) => new(options, null);
}

/// <summary>
/// A small hand-rolled parser. Monad's surface is a handful of flags, and
/// hand-rolling keeps the tool dependency-free and its behaviour easy to test.
/// </summary>
public static class CommandLineParser
{
    public const string TokenVariable = "AUDD_API_TOKEN";

    /// <summary>
    /// Resolves the command line together with the config file. Precedence,
    /// strongest first: command line, environment, config file, defaults.
    /// </summary>
    public static ParseResult Parse(IReadOnlyList<string> args) =>
        Parse(args, Environment.GetEnvironmentVariable, ConfigFile.Discover);

    public static ParseResult Parse(
        IReadOnlyList<string> args,
        Func<string, string?> readEnvironment,
        Func<string?, (ConfigFile Config, string? Path)> discoverConfig)
    {
        ArgumentNullException.ThrowIfNull(args);

        MonadCommand? command = null;
        string? token = null;
        string? configPath = null;
        string? jsonlPath = null;
        string? inputPath = null;
        AudioSourceKind? source = null;
        bool noRecognize = false;
        bool verbose = false;
        bool quiet = false;

        int? windowSeconds = null;
        int? intervalSeconds = null;
        int? minSegmentSeconds = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith('-'))
            {
                if (command is null)
                {
                    if (!TryParseCommand(arg, out MonadCommand parsed))
                    {
                        return ParseResult.Failed($"Unknown command '{arg}'.");
                    }

                    command = parsed;
                }
                else if (inputPath is null)
                {
                    inputPath = arg;
                }
                else
                {
                    return ParseResult.Failed($"Unexpected argument '{arg}'.");
                }

                continue;
            }

            // Accept both "--flag value" and "--flag=value".
            string name = arg;
            string? inlineValue = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            switch (name)
            {
                case "--help" or "-h":
                    command = MonadCommand.Help;
                    break;

                case "--version" or "-V":
                    command = MonadCommand.Version;
                    break;

                case "--no-recognize" or "--dry-run":
                    noRecognize = true;
                    break;

                case "--verbose" or "-v":
                    verbose = true;
                    break;

                case "--quiet" or "-q":
                    quiet = true;
                    break;

                case "--token":
                    if (!TryTakeValue(args, ref i, inlineValue, name, out token, out string? error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--config":
                    if (!TryTakeValue(args, ref i, inlineValue, name, out configPath, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--jsonl":
                    if (!TryTakeValue(args, ref i, inlineValue, name, out jsonlPath, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--input":
                    if (!TryTakeValue(args, ref i, inlineValue, name, out inputPath, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--source":
                    if (!TryTakeValue(args, ref i, inlineValue, name, out string? rawSource, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    if (!TryParseSource(rawSource!, out AudioSourceKind kind))
                    {
                        return ParseResult.Failed(
                            $"Unknown --source '{rawSource}'. Use auto, macos-tap, wasapi or pulse.");
                    }

                    source = kind;
                    break;

                case "--window":
                    if (!TryTakeInt(args, ref i, inlineValue, name, out windowSeconds, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--interval":
                    if (!TryTakeInt(args, ref i, inlineValue, name, out intervalSeconds, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                case "--min-segment":
                    if (!TryTakeInt(args, ref i, inlineValue, name, out minSegmentSeconds, out error))
                    {
                        return ParseResult.Failed(error!);
                    }

                    break;

                default:
                    return ParseResult.Failed($"Unknown option '{name}'.");
            }
        }

        MonadCommand resolved = command ?? MonadCommand.Run;

        if (resolved == MonadCommand.Replay && string.IsNullOrWhiteSpace(inputPath))
        {
            return ParseResult.Failed("'monad replay' needs the path to a WAV file.");
        }

        ConfigFile file;
        string? loadedFrom;

        try
        {
            (file, loadedFrom) = discoverConfig(configPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            return ParseResult.Failed(ex.Message);
        }

        if (source is null && file.Source is { Length: > 0 } fileSource)
        {
            if (!TryParseSource(fileSource, out AudioSourceKind fromFile))
            {
                return ParseResult.Failed($"Config file has an unknown source '{fileSource}'.");
            }

            source = fromFile;
        }

        TuningOverrides overrides = new()
        {
            WindowSeconds = windowSeconds,
            IntervalSeconds = intervalSeconds,
            MinSegmentDurationSeconds = minSegmentSeconds,
        };

        MonadConfig config = overrides.ApplyTo(file.Tuning ?? new MonadConfig());

        try
        {
            config.Validate();
        }
        catch (ArgumentException ex)
        {
            string where = loadedFrom is null ? "Configuration" : $"Configuration ({loadedFrom})";
            return ParseResult.Failed($"{where} is invalid: {ex.Message}");
        }

        return ParseResult.Ok(new MonadOptions
        {
            Command = resolved,
            Token = token ?? readEnvironment(TokenVariable) ?? file.Token,
            ConfigPath = loadedFrom,
            JsonlPath = jsonlPath ?? file.Jsonl,
            InputPath = inputPath,
            Source = source ?? AudioSourceKind.Auto,
            NoRecognize = noRecognize,
            Verbose = verbose,
            Quiet = quiet,
            Config = config,
        });
    }

    private static bool TryParseCommand(string value, out MonadCommand command)
    {
        switch (value.ToLowerInvariant())
        {
            case "run": command = MonadCommand.Run; return true;
            case "devices": command = MonadCommand.Devices; return true;
            case "replay": command = MonadCommand.Replay; return true;
            case "help": command = MonadCommand.Help; return true;
            case "version": command = MonadCommand.Version; return true;
            default: command = MonadCommand.Run; return false;
        }
    }

    private static bool TryParseSource(string value, out AudioSourceKind kind)
    {
        switch (value.ToLowerInvariant())
        {
            case "auto": kind = AudioSourceKind.Auto; return true;
            case "macos-tap" or "mac-tap" or "tap" or "macos": kind = AudioSourceKind.MacTap; return true;
            case "wasapi" or "windows" or "loopback": kind = AudioSourceKind.Wasapi; return true;
            case "pulse" or "pulseaudio" or "pipewire" or "monitor": kind = AudioSourceKind.PulseMonitor; return true;
            default: kind = AudioSourceKind.Auto; return false;
        }
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> args,
        ref int index,
        string? inlineValue,
        string name,
        out string? value,
        out string? error)
    {
        value = inlineValue ?? (index + 1 < args.Count ? args[++index] : null);

        if (string.IsNullOrEmpty(value))
        {
            value = null;
            error = $"{name} needs a value.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryTakeInt(
        IReadOnlyList<string> args,
        ref int index,
        string? inlineValue,
        string name,
        out int? value,
        out string? error)
    {
        value = null;

        if (!TryTakeValue(args, ref index, inlineValue, name, out string? raw, out error))
        {
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"{name} expects a whole number of seconds, but got '{raw}'.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static string Usage =>
        """
        monad -- watch what this machine is playing and log it as a timeline

        USAGE
          monad [run] [options]           Monitor system audio (default)
          monad devices                   Show the capture path and available devices
          monad replay <file.wav>         Run a recording through the same analysis
          monad help | version

        OPTIONS
          --token <token>       AudD API token (or set AUDD_API_TOKEN)
          --config <path>       Config file; defaults to ./monad.json then
                                ~/.config/monad/config.json
          --jsonl <path>        Append each finished segment to a JSON Lines file
          --source <name>       auto (default), macos-tap, wasapi, pulse
          --window <seconds>    Audio analysed per lookup (default 12)
          --interval <seconds>  Seconds between lookups (default 15)
          --min-segment <secs>  Drop segments shorter than this (default 30)
          --no-recognize        Skip the API entirely; capture and levels only
          -v, --verbose         Show per-window detail
          -q, --quiet           Print segments only
          -h, --help            Show this help
        """;
}
