using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

using Monad.Audio.Sources;

namespace Monad.Cli;

/// <summary>Something to print instead of running: the help, the version or a mistake.</summary>
public sealed record class CliMessage(StyledText Text, bool ToError, int ExitCode);

/// <summary>What the command line resolved to: options to run with, or a message to print.</summary>
public sealed record class CommandLineResult(MonadOptions? Options, CliMessage? Message)
{
    /// <summary>The parsed global color preference, including explicit boolean values.</summary>
    public bool NoColor { get; init; }

    /// <summary>The words of a failure; null when monad runs or only shows text.</summary>
    public string? Error => this.Message is { ExitCode: not 0 } message ? message.Text.ToString() : null;
}

/// <summary>
/// The command line. System.CommandLine parses it; monad prints its own help,
/// version and mistakes, because the library's defaults break the playbook's
/// rules: its help has no colors, its version leaves out the name, and its
/// mistakes exit 1.
/// </summary>
public static class CommandLineParser
{
    public const string TokenVariable = "AUDD_API_TOKEN";

    /// <summary>The repo's GitHub description as a sentence: monad's help and its repo describe it in the same words.</summary>
    public const string Description = "Watches what your machine is playing and logs it as a timeline.";

    /// <summary>Exit code for a wrong command, option or value.</summary>
    public const int UsageError = 2;

    /// <summary>Exit code for something that failed while running, such as an unreadable config file.</summary>
    public const int Failure = 1;

    /// <summary>What a bare <c>monad</c> says after its help when it has no token to monitor with.</summary>
    public const string MissingToken = "To start monitoring, set " + TokenVariable + " or pass --token.";

    /// <summary>
    /// Resolves the command line together with the config file. Precedence,
    /// strongest first: command line, environment, config file, defaults.
    /// </summary>
    public static CommandLineResult Parse(IReadOnlyList<string> args) =>
        Parse(args, Environment.GetEnvironmentVariable, ConfigFile.Discover);

    public static CommandLineResult Parse(
        IReadOnlyList<string> args,
        Func<string, string?> readEnvironment,
        Func<string?, (ConfigFile Config, string? Path)> discoverConfig)
    {
        ArgumentNullException.ThrowIfNull(args);

        Definitions cli = new();

        // `@file` would otherwise be read as a file of more arguments.
        ParseResult parsed = cli.Root.Parse([.. args], new ParserConfiguration { ResponseFileTokenReplacer = null });
        return Resolve(args, readEnvironment, discoverConfig, cli, parsed) with { NoColor = parsed.GetValue(cli.NoColor) };
    }

    private static CommandLineResult Resolve(
        IReadOnlyList<string> args,
        Func<string, string?> readEnvironment,
        Func<string?, (ConfigFile Config, string? Path)> discoverConfig,
        Definitions cli,
        ParseResult parsed)
    {
        Command command = parsed.CommandResult.Command;

        if (parsed.GetValue(cli.Help))
        {
            return Show(HelpText.For(command, cli.Root));
        }

        if (parsed.Errors.Count > 0)
        {
            return Mistake(parsed.Errors.Select(error => error.Message));
        }

        if (parsed.GetValue(cli.ShowVersion) || command == cli.Version)
        {
            return Show(new StyledText().Add($"monad {Version()}\n"));
        }

        if (command == cli.HelpCommand)
        {
            string? topic = parsed.GetValue(cli.Topic);
            Command? target = topic is null
                ? cli.Root
                : cli.Root.Subcommands.FirstOrDefault(sub => sub.Name == topic || sub.Aliases.Contains(topic));

            return target is null
                ? Mistake([$"'{topic}' is not a monad command."])
                : Show(HelpText.For(target, cli.Root));
        }

        if (command == cli.Update)
        {
            return new(new MonadOptions { Command = MonadCommand.Update }, null);
        }

        MonadCommand resolved = command == cli.Devices
            ? MonadCommand.Devices
            : command == cli.Replay ? MonadCommand.Replay : MonadCommand.Run;

        string? fileArgument = parsed.GetValue(cli.File);
        string? fileOption = parsed.GetValue(cli.Input);

        if (fileArgument is not null && fileOption is not null)
        {
            return Mistake(["'monad replay' takes one WAV file, either as its argument or with --input."]);
        }

        string? inputPath = fileArgument ?? fileOption;

        if (resolved == MonadCommand.Replay && string.IsNullOrWhiteSpace(inputPath))
        {
            return Mistake(["'monad replay' needs the path to a WAV file."]);
        }

        ConfigFile file;
        string? loadedFrom;
        string? configPath = parsed.GetValue(cli.Config);

        try
        {
            (file, loadedFrom) = discoverConfig(configPath);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(configPath) && ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only an explicit --config can be missing: the command line named it.
            return Mistake([ex.Message]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Failed(ex.Message);
        }

        AudioSourceKind? source = parsed.GetValue(cli.Source);

        if (source is null && file.Source is { Length: > 0 } fileSource)
        {
            if (!TryParseSource(fileSource, out AudioSourceKind fromFile))
            {
                return Failed($"Config file has an unknown source '{fileSource}'.");
            }

            source = fromFile;
        }

        TuningOverrides overrides = new()
        {
            WindowSeconds = parsed.GetValue(cli.Window),
            IntervalSeconds = parsed.GetValue(cli.Interval),
            MinSegmentDurationSeconds = parsed.GetValue(cli.MinSegment),
        };

        MonadConfig config = overrides.ApplyTo(file.Tuning ?? new MonadConfig());

        try
        {
            config.Validate();
        }
        catch (ArgumentException ex)
        {
            string where = loadedFrom is null ? "Configuration" : $"Configuration ({loadedFrom})";
            string message = $"{where} is invalid: {ex.Message}";

            // A tuning value named on the command line is a wrong value there;
            // otherwise the config file is what can't be used.
            bool fromCommandLine = overrides.WindowSeconds is not null
                || overrides.IntervalSeconds is not null
                || overrides.MinSegmentDurationSeconds is not null;

            if (fromCommandLine)
            {
                try
                {
                    (file.Tuning ?? new MonadConfig()).Validate();
                }
                catch (ArgumentException)
                {
                    // An unrelated CLI override cannot turn a broken config
                    // file into an argument error. A fully repaired config
                    // already passed validation above.
                    fromCommandLine = false;
                }
            }

            return fromCommandLine ? Mistake([message]) : Failed(message);
        }

        string? token = parsed.GetValue(cli.Token) ?? readEnvironment(TokenVariable) ?? file.Token;

        // Only a fully bare `monad` falls back to its help: WinGet's validation
        // starts every new version that way, without a token.
        if (args.Count == 0 && string.IsNullOrWhiteSpace(token))
        {
            return Show(HelpText.For(cli.Root, cli.Root).Add($"\n{MissingToken}\n"));
        }

        return new(
            new MonadOptions
            {
                Command = resolved,
                Token = token,
                ConfigPath = loadedFrom,
                JsonlPath = ExpandHome(parsed.GetValue(cli.Jsonl) ?? file.Jsonl),
                InputPath = ExpandHome(inputPath),
                Source = source ?? AudioSourceKind.Auto,
                NoRecognize = parsed.GetValue(cli.NoRecognize),
                Verbose = parsed.GetValue(cli.Verbose),
                Quiet = parsed.GetValue(cli.Quiet),
                Config = config,
            },
            null);
    }

    /// <summary>The version monad reports, from its assembly.</summary>
    public static string Version()
    {
        Assembly assembly = typeof(CommandLineParser).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>
    /// Expands a leading <c>~</c> to the home directory.
    /// </summary>
    /// <remarks>
    /// The shell does this for command line arguments, but a path written in a
    /// config file never passes through one -- so <c>"~/listening.jsonl"</c>
    /// would otherwise be taken literally and create a directory called "~".
    /// </remarks>
    public static string? ExpandHome(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.Length == 1)
        {
            return home;
        }

        // "~otheruser/..." means someone else's home, which is not supported.
        return path[1] == '/' || path[1] == Path.DirectorySeparatorChar
            ? Path.Combine(home, path[2..])
            : path;
    }

    private static CommandLineResult Show(StyledText text) => new(null, new CliMessage(text, ToError: false, 0));

    private static CommandLineResult Mistake(IEnumerable<string> messages)
    {
        StyledText text = new();

        foreach (string message in messages)
        {
            text.Add($"monad: {message}\n");
        }

        text.Add("Run 'monad --help' for usage.\n");

        return new(null, new CliMessage(text, ToError: true, UsageError));
    }

    private static CommandLineResult Failed(string message) =>
        new(null, new CliMessage(new StyledText().Add($"monad: {message}\n"), ToError: true, Failure));

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

    private static AudioSourceKind? ParseSource(ArgumentResult result)
    {
        string raw = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;

        if (TryParseSource(raw, out AudioSourceKind kind))
        {
            return kind;
        }

        result.AddError($"Unknown --source '{raw}'. Use auto, macos-tap, wasapi or pulse.");
        return null;
    }

    private static Option<int?> Seconds(string name, string description, int minimum = 1) => new(name)
    {
        Description = description,
        HelpName = "seconds",
        CustomParser = result =>
        {
            string raw = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                if (seconds < minimum)
                {
                    result.AddError($"{name} must be at least {minimum} seconds.");
                    return null;
                }

                return seconds;
            }

            result.AddError($"{name} expects a whole number of seconds, but got '{raw}'.");
            return null;
        },
    };

    /// <summary>
    /// Every command and option, built fresh for each parse because
    /// System.CommandLine's symbols remember the commands they were added to.
    /// </summary>
    private sealed class Definitions
    {
        public readonly Option<string> Token = new("--token")
        {
            Description = $"AudD API token (or set {TokenVariable})",
            HelpName = "token",
        };

        public readonly Option<string> Config = new("--config")
        {
            Description = "Config file; defaults to ./monad.json, then ~/.config/monad/config.json",
            HelpName = "path",
            CustomParser = result =>
            {
                string? path = result.Tokens.Count > 0 ? result.Tokens[0].Value : null;

                if (string.IsNullOrWhiteSpace(path))
                {
                    result.AddError("--config needs a nonempty file path.");
                }

                return path;
            },
        };

        public readonly Option<string> Jsonl = new("--jsonl")
        {
            Description = "Append each finished segment to a JSON Lines file",
            HelpName = "path",
        };

        public readonly Option<AudioSourceKind?> Source = new("--source")
        {
            Description = "auto (default), macos-tap, wasapi, pulse",
            HelpName = "name",
            CustomParser = ParseSource,
        };

        public readonly Option<int?> Window = Seconds("--window", "Audio analysed per lookup (default 12)");

        public readonly Option<int?> Interval = Seconds("--interval", "Seconds between lookups (default 15)");

        public readonly Option<int?> MinSegment = Seconds("--min-segment", "Drop segments shorter than this (default 30)", minimum: 0);

        public readonly Option<bool> NoRecognize = new("--no-recognize", "--dry-run")
        {
            Description = "Skip the API entirely; capture and levels only",
        };

        public readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "Show per-window detail" };

        public readonly Option<bool> Quiet = new("--quiet", "-q") { Description = "Print segments only" };

        public readonly Option<string> Input = new("--input")
        {
            Description = "The WAV file, instead of the argument",
            HelpName = "file.wav",
        };

        public readonly Option<bool> NoColor = new("--no-color")
        {
            Description = "Print without colors, like NO_COLOR=1",
            Recursive = true,
        };

        public readonly Option<bool> Help = new("--help", "-h") { Description = "Show this help", Recursive = true };

        public readonly Option<bool> ShowVersion = new("--version", "-V") { Description = "Print the version" };

        public readonly Argument<string?> File = new("file.wav")
        {
            Description = "The recording to analyse",
            Arity = ArgumentArity.ZeroOrOne,
        };

        public readonly Argument<string?> Topic = new("command")
        {
            Description = "The command to explain",
            Arity = ArgumentArity.ZeroOrOne,
        };

        public readonly RootCommand Root = new(Description);

        public readonly Command Run = new("run", "Monitor system audio (the default)");

        public readonly Command Devices = new("devices", "Show the capture path and available devices");

        public readonly Command Replay = new("replay", "Run a recording through the same analysis");

        public readonly Command Update = new("update", "Update monad to the latest release");

        public readonly Command Version = new("version", "Print the version");

        public readonly Command HelpCommand = new("help", "Show this help, or the help of one command");

        public Definitions()
        {
            // monad prints its own help and version, and has no use for directives.
            this.Root.Options.Clear();
            this.Root.Directives.Clear();

            // With an action, the root runs without a command: that's `monad`, which monitors.
            this.Root.SetAction(_ => 0);

            Option[] monitoring =
            [
                this.Token, this.Config, this.Jsonl, this.Source, this.Window, this.Interval,
                this.MinSegment, this.NoRecognize, this.Verbose, this.Quiet,
            ];

            AddOptions(this.Root, [.. monitoring, this.NoColor, this.Help, this.ShowVersion]);
            AddOptions(this.Run, monitoring);
            AddOptions(this.Devices, [this.Config, this.Source, this.Verbose]);
            this.Replay.Arguments.Add(this.File);
            AddOptions(
                this.Replay,
                [
                    this.Input, this.Token, this.Config, this.Jsonl, this.Window, this.Interval,
                    this.MinSegment, this.NoRecognize, this.Verbose, this.Quiet,
                ]);
            this.HelpCommand.Arguments.Add(this.Topic);
            this.Update.Aliases.Add("upgrade");

            foreach (Command command in new[] { this.Run, this.Devices, this.Replay, this.Update, this.Version, this.HelpCommand })
            {
                this.Root.Subcommands.Add(command);
            }
        }

        private static void AddOptions(Command command, IEnumerable<Option> options)
        {
            foreach (Option option in options)
            {
                command.Options.Add(option);
            }
        }
    }
}
