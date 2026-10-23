using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FluentAssertions;

using Monad;
using Monad.Audio.Sources;
using Monad.Cli;

using Xunit;

namespace Monad.Tests.Cli;

public sealed class CommandLineParserTests
{
    private static readonly Dictionary<string, string?> WithToken = new() { [CommandLineParser.TokenVariable] = "t" };

    private static CommandLineResult Parse(
        string[] args, ConfigFile? file = null, Dictionary<string, string?>? environment = null) =>
        CommandLineParser.Parse(
            args,
            name => environment is not null && environment.TryGetValue(name, out string? value) ? value : null,
            _ => (file ?? ConfigFile.Empty, file is null ? null : "test-config.json"));

    private static MonadOptions Ok(string[] args, ConfigFile? file = null, Dictionary<string, string?>? env = null)
    {
        CommandLineResult result = Parse(args, file, env);
        result.Message.Should().BeNull();
        return result.Options!;
    }

    /// <summary>What monad prints instead of running, as plain text.</summary>
    private static CliMessage Shown(string[] args, ConfigFile? file = null, Dictionary<string, string?>? env = null)
    {
        CommandLineResult result = Parse(args, file, env);
        result.Options.Should().BeNull();
        return result.Message!;
    }

    // --- Commands ---

    [Fact]
    public void Parse_WithNoArgumentsAndAToken_Monitors()
    {
        Ok([], env: WithToken).Command.Should().Be(MonadCommand.Run);
        Ok([], file: new ConfigFile { Token = "from-file" }).Command.Should().Be(MonadCommand.Run);
    }

    [Fact]
    public void Parse_WithNoArgumentsAndNoToken_ShowsHelpAndHowToSetOne()
    {
        // Arrange / Act — WinGet's validation starts every new version this way
        CliMessage message = Shown([]);

        // Assert
        message.ExitCode.Should().Be(0);
        message.ToError.Should().BeFalse();
        message.Text.ToString().Should().Be(Shown(["--help"]).Text + "\n" + CommandLineParser.MissingToken + "\n");
    }

    [Fact]
    public void Parse_WithNoArgumentsAndAnUnreadableConfigFile_Fails()
    {
        // Arrange — a config file that exists but can't be read is an error, even for a bare command
        CommandLineResult result = CommandLineParser.Parse(
            [], _ => null, _ => throw new System.IO.InvalidDataException("Config file 'monad.json' is not valid JSON"));

        // Assert
        result.Message!.ExitCode.Should().Be(CommandLineParser.Failure);
        result.Error.Should().Contain("not valid JSON");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_WithAConfigReadFailure_ReportsExitOne(bool accessDenied)
    {
        Exception failure = accessDenied ? new UnauthorizedAccessException("cannot read config") : new IOException("cannot read config");

        CommandLineResult result = CommandLineParser.Parse([], _ => null, _ => throw failure);

        result.Message!.ExitCode.Should().Be(CommandLineParser.Failure);
        result.Message.ToError.Should().BeTrue();
        result.Error.Should().Contain("cannot read config");
    }

    [Fact]
    public void Parse_WithNullVoteWeights_ReportsAnInvalidConfig()
    {
        ConfigFile file = new() { Tuning = new MonadConfig { VoteWeightsNewestToOldest = null! } };

        CliMessage message = Shown([], file);

        message.ExitCode.Should().Be(CommandLineParser.Failure);
        message.Text.ToString().Should().Contain("voteWeightsNewestToOldest");
    }

    [Fact]
    public void Parse_WithOnlyAnOption_IsNotBare()
    {
        // An explicit command line runs, and fails later without a token.
        Ok(["--verbose"]).Token.Should().BeNull();
    }

    [Theory]
    [InlineData("run", MonadCommand.Run)]
    [InlineData("devices", MonadCommand.Devices)]
    [InlineData("update", MonadCommand.Update)]
    [InlineData("upgrade", MonadCommand.Update)]
    public void Parse_RecognisesEachCommand(string arg, MonadCommand expected)
    {
        Ok([arg]).Command.Should().Be(expected);
    }

    [Fact]
    public void Parse_WithAnUnknownCommand_Fails()
    {
        Parse(["sing"]).Error.Should().Contain("sing");
    }

    [Fact]
    public void Parse_WithAnUnknownOption_Fails()
    {
        Parse(["--wat"]).Error.Should().Contain("--wat");
    }

    // --- Replay ---

    [Fact]
    public void Parse_ReplayTakesItsFileAsAPositionalArgument()
    {
        // Arrange / Act
        MonadOptions options = Ok(["replay", "clip.wav"]);

        // Assert
        options.Command.Should().Be(MonadCommand.Replay);
        options.InputPath.Should().Be("clip.wav");
    }

    [Fact]
    public void Parse_ReplayAlsoAcceptsTheFileAsAnOption()
    {
        Ok(["replay", "--input", "clip.wav"]).InputPath.Should().Be("clip.wav");
    }

    [Fact]
    public void Parse_ReplayWithoutAFile_Fails()
    {
        Parse(["replay"]).Error.Should().Contain("WAV");
    }

    [Fact]
    public void Parse_WithATrailingExtraArgument_Fails()
    {
        Parse(["replay", "a.wav", "b.wav"]).Error.Should().Contain("b.wav");
    }

    // --- Flags ---

    [Fact]
    public void Parse_ReadsTheSimpleFlags()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run", "--no-recognize", "--verbose", "--quiet"]);

        // Assert
        options.NoRecognize.Should().BeTrue();
        options.Verbose.Should().BeTrue();
        options.Quiet.Should().BeTrue();
    }

    [Fact]
    public void Parse_AcceptsShortFlags()
    {
        // Arrange / Act
        MonadOptions options = Ok(["-v", "-q"]);

        // Assert
        options.Verbose.Should().BeTrue();
        options.Quiet.Should().BeTrue();
    }

    [Fact]
    public void Parse_TreatsDryRunAsNoRecognize()
    {
        Ok(["--dry-run"]).NoRecognize.Should().BeTrue();
    }

    // --- Values ---

    [Fact]
    public void Parse_ReadsValuesGivenAsSeparateArguments()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run", "--token", "abc", "--jsonl", "out.jsonl"]);

        // Assert
        options.Token.Should().Be("abc");
        options.JsonlPath.Should().Be("out.jsonl");
    }

    [Fact]
    public void Parse_ReadsValuesGivenWithEquals()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run", "--token=abc", "--jsonl=out.jsonl"]);

        // Assert
        options.Token.Should().Be("abc");
        options.JsonlPath.Should().Be("out.jsonl");
    }

    [Fact]
    public void Parse_WithAValueMissing_Fails()
    {
        Parse(["run", "--token"]).Error.Should().Contain("--token");
    }

    [Theory]
    [InlineData("auto", AudioSourceKind.Auto)]
    [InlineData("macos-tap", AudioSourceKind.MacTap)]
    [InlineData("tap", AudioSourceKind.MacTap)]
    [InlineData("wasapi", AudioSourceKind.Wasapi)]
    [InlineData("WASAPI", AudioSourceKind.Wasapi)]
    [InlineData("pulse", AudioSourceKind.PulseMonitor)]
    [InlineData("pipewire", AudioSourceKind.PulseMonitor)]
    public void Parse_RecognisesEachSourceName(string name, AudioSourceKind expected)
    {
        Ok(["run", "--source", name]).Source.Should().Be(expected);
    }

    [Fact]
    public void Parse_WithAnUnknownSource_Fails()
    {
        Parse(["run", "--source", "carrier-pigeon"]).Error.Should().Contain("carrier-pigeon");
    }

    // --- Tuning ---

    [Fact]
    public void Parse_AppliesTuningOverrides()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run", "--window", "20", "--interval", "10", "--min-segment", "45"]);

        // Assert
        options.Config.WindowSeconds.Should().Be(20);
        options.Config.IntervalSeconds.Should().Be(10);
        options.Config.MinSegmentDurationSeconds.Should().Be(45);
    }

    [Fact]
    public void Parse_LeavesUnmentionedTuningAtItsDefault()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run", "--window", "20"]);

        // Assert
        options.Config.IntervalSeconds.Should().Be(new MonadConfig().IntervalSeconds);
    }

    [Fact]
    public void Parse_WithANonNumericTuningValue_Fails()
    {
        Parse(["run", "--window", "soon"]).Error.Should().Contain("soon");
    }

    [Theory]
    [InlineData("60")]
    [InlineData("600000000")]
    public void Parse_WithTuningThatCannotWork_Fails(string interval)
    {
        // Arrange — five samples this far apart cannot fit in the default 120s window
        CommandLineResult result = Parse(["run", "--interval", interval]);

        // Assert — the command line asked for it, so it's a mistake there
        result.Error.Should().Contain("ringBufferMaxAgeSeconds");
        result.Message!.ExitCode.Should().Be(CommandLineParser.UsageError);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(600000000)]
    public void Parse_WithAConfigFileThatCannotWork_Fails(int interval)
    {
        // Arrange
        ConfigFile file = new() { Tuning = new MonadConfig { IntervalSeconds = interval } };

        // Act
        CommandLineResult result = Parse(["run"], file);

        // Assert — nothing on the command line is wrong; the file can't be used
        result.Error.Should().Contain("test-config.json");
        result.Message!.ExitCode.Should().Be(CommandLineParser.Failure);
    }

    [Theory]
    [InlineData("--window", "3")]
    [InlineData("--interval", "3")]
    [InlineData("--min-segment", "0")]
    public void Parse_WithAnUnrelatedTuningOverride_KeepsTheConfigFailure(string option, string value)
    {
        ConfigFile file = new() { Tuning = new MonadConfig { PreferredSampleRate = 0 } };

        CliMessage message = Shown([option, value], file);

        message.ExitCode.Should().Be(CommandLineParser.Failure);
        message.Text.ToString().Should().Contain("preferredSampleRate");
    }

    [Fact]
    public void Parse_WithAnOverrideThatRepairsTheConfig_Runs()
    {
        ConfigFile file = new() { Tuning = new MonadConfig { IntervalSeconds = 60 } };

        MonadOptions options = Ok(["run", "--interval", "15"], file);

        options.Config.IntervalSeconds.Should().Be(15);
    }

    [Theory]
    [InlineData("--window", "0")]
    [InlineData("--interval", "0")]
    [InlineData("--min-segment", "-1")]
    public void Parse_WithAnInvalidTuningArgument_RejectsItBeforeReadingConfig(string option, string value)
    {
        CommandLineResult result = CommandLineParser.Parse(
            [option, value], _ => null, _ => throw new InvalidDataException("broken config"));

        result.Message!.ExitCode.Should().Be(CommandLineParser.UsageError);
        result.Error.Should().Contain(option).And.NotContain("broken config");
    }

    // --- Precedence ---

    [Fact]
    public void Parse_PrefersTheCommandLineTokenOverEverythingElse()
    {
        // Arrange / Act
        MonadOptions options = Ok(
            ["run", "--token", "from-cli"],
            file: new ConfigFile { Token = "from-file" },
            env: new Dictionary<string, string?> { [CommandLineParser.TokenVariable] = "from-env" });

        // Assert
        options.Token.Should().Be("from-cli");
    }

    [Fact]
    public void Parse_PrefersTheEnvironmentTokenOverTheConfigFile()
    {
        // Arrange / Act
        MonadOptions options = Ok(
            ["run"],
            file: new ConfigFile { Token = "from-file" },
            env: new Dictionary<string, string?> { [CommandLineParser.TokenVariable] = "from-env" });

        // Assert
        options.Token.Should().Be("from-env");
    }

    [Fact]
    public void Parse_FallsBackToTheConfigFileToken()
    {
        Ok(["run"], file: new ConfigFile { Token = "from-file" }).Token.Should().Be("from-file");
    }

    [Fact]
    public void Parse_TakesJsonlAndSourceFromTheConfigFile()
    {
        // Arrange / Act
        MonadOptions options = Ok(["run"], file: new ConfigFile { Jsonl = "log.jsonl", Source = "pulse" });

        // Assert
        options.JsonlPath.Should().Be("log.jsonl");
        options.Source.Should().Be(AudioSourceKind.PulseMonitor);
    }

    [Fact]
    public void Parse_LetsTheCommandLineOverrideConfigFileTuning()
    {
        // Arrange
        ConfigFile file = new() { Tuning = new MonadConfig { WindowSeconds = 30, IntervalSeconds = 20 } };

        // Act
        MonadOptions options = Ok(["run", "--window", "8"], file);

        // Assert
        options.Config.WindowSeconds.Should().Be(8);
        options.Config.IntervalSeconds.Should().Be(20); // untouched by the command line
    }

    [Fact]
    public void Parse_WithAnUnknownSourceInTheConfigFile_Fails()
    {
        CommandLineResult result = Parse(["run"], file: new ConfigFile { Source = "gramophone" });

        result.Error.Should().Contain("gramophone");
        result.Message!.ExitCode.Should().Be(CommandLineParser.Failure);
    }

    [Fact]
    public void Parse_ReportsWhereTheConfigCameFrom()
    {
        Ok(["run"], file: new ConfigFile()).ConfigPath.Should().Be("test-config.json");
    }

    // --- Home directory expansion ---

    [Fact]
    public void Parse_ExpandsATildeInAConfigFilePath()
    {
        // Arrange — a config file path never passes through a shell, so the
        // tilde arrives literally and has to be expanded here instead.
        MonadOptions options = Ok(["run"], file: new ConfigFile { Jsonl = "~/listening.jsonl" });

        // Assert
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        options.JsonlPath.Should().Be(System.IO.Path.Combine(home, "listening.jsonl"));
    }

    [Fact]
    public void Parse_ExpandsATildeGivenOnTheCommandLine()
    {
        // Arrange — reachable when the argument was quoted against the shell
        MonadOptions options = Ok(["replay", "~/clip.wav"]);

        // Assert
        options.InputPath.Should().NotStartWith("~");
        options.InputPath.Should().EndWith("clip.wav");
    }

    [Theory]
    [InlineData("/absolute/path.jsonl")]
    [InlineData("relative/path.jsonl")]
    [InlineData("~otheruser/path.jsonl")] // another user's home is not supported
    public void Parse_LeavesOtherPathsAlone(string path)
    {
        Ok(["run", "--jsonl", path]).JsonlPath.Should().Be(path);
    }

    [Fact]
    public void ExpandHome_WithABareTilde_ReturnsTheHomeDirectory()
    {
        CommandLineParser.ExpandHome("~")
            .Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExpandHome_WithNothingToExpand_ReturnsItUnchanged(string? path)
    {
        CommandLineParser.ExpandHome(path).Should().Be(path);
    }

    // --- Help, version and mistakes ---

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Parse_HelpFlagsAndTheHelpCommand_ShowTheSameHelp(string arg)
    {
        // Act
        CliMessage message = Shown([arg]);

        // Assert
        message.ExitCode.Should().Be(0);
        message.ToError.Should().BeFalse();
        message.Text.ToString().Should().StartWith(CommandLineParser.Description + "\n\nUsage: monad [options]");
    }

    [Fact]
    public void Parse_HelpOfOneCommand_IsTheSameEitherWay()
    {
        string expected = Shown(["replay", "--help"]).Text.ToString();

        expected.Should().StartWith("Run a recording through the same analysis\n\nUsage: monad replay [options] [<file.wav>]");
        Shown(["help", "replay"]).Text.ToString().Should().Be(expected);
        Shown(["replay", "-h"]).Text.ToString().Should().Be(expected);
        Shown(["help", "upgrade"]).Text.ToString().Should().Be(Shown(["update", "--help"]).Text.ToString());
    }

    [Fact]
    public void Parse_HelpAfterAConfigFile_StillShowsHelp()
    {
        // The release smoke test runs exactly this.
        Shown(["--config", "monad.example.json", "help"]).ExitCode.Should().Be(0);
    }

    [Fact]
    public void Help_ListsEveryCommandAndOptionOnce()
    {
        // Arrange
        string[] lines = Shown(["--help"]).Text.ToString().Split('\n');

        // Assert — including the ones the old hand-written help left out
        foreach (string expected in new[]
        {
            "  run      Monitor system audio (the default)",
            "  devices  Show the capture path and available devices",
            "  replay   Run a recording through the same analysis",
            "  update   Update monad to the latest release",
            "  version  Print the version",
            "  help     Show this help, or the help of one command",
            "      --no-recognize, --dry-run  Skip the API entirely; capture and levels only",
            "  -v, --verbose                  Show per-window detail",
            "  -h, --help                     Show this help",
            "  -V, --version                  Print the version",
        })
        {
            lines.Count(line => line == expected).Should().Be(1, $"'{expected}' appears once");
        }

        Shown(["help", "replay"]).Text.ToString().Should().Contain("--input <file.wav>");
        string.Join('\n', lines).Should().NotContain("upgrade", "the alias stays out of the help");
    }

    [Fact]
    public void Help_UsesArgparseColorsOnlyWhenAsked()
    {
        // Arrange
        StyledText help = Shown(["--help"]).Text;

        // Assert
        help.Render(color: false).Should().NotContain("\u001b");
        help.Render(color: true).Should()
            .Contain(StyledText.Heading + "Usage:")
            .And.Contain(StyledText.ProgramName + "monad")
            .And.Contain(StyledText.CommandName + "replay")
            .And.Contain(StyledText.ShortOption + "-v")
            .And.Contain(StyledText.LongOption + "--verbose")
            .And.Contain(StyledText.Value + "<token>")
            .And.Contain(StyledText.UsageValue + "[options]");
    }

    [Theory]
    [InlineData("-V")]
    [InlineData("--version")]
    [InlineData("version")]
    public void Parse_EveryVersionForm_PrintsTheNameAndVersion(string arg)
    {
        CliMessage message = Shown([arg]);

        message.ExitCode.Should().Be(0);
        message.ToError.Should().BeFalse();
        message.Text.ToString().Should().Be($"monad {CommandLineParser.Version()}\n");
    }

    [Theory]
    [InlineData("sing")]
    [InlineData("--wat")]
    [InlineData("run", "--token")]
    [InlineData("run", "--window", "soon")]
    [InlineData("run", "--source", "carrier-pigeon")]
    [InlineData("replay")]
    [InlineData("replay", "a.wav", "b.wav")]
    [InlineData("replay", "a.wav", "--input", "b.wav")]
    [InlineData("help", "sing")]
    [InlineData("devices", "--token", "t")]
    [InlineData("@arguments.txt")]
    [InlineData("--config", "")]
    [InlineData("--config", " ")]
    public void Parse_Mistakes_ExitTwoWithAPointerToHelp(params string[] args)
    {
        CliMessage message = Shown(args, env: WithToken);

        message.ExitCode.Should().Be(CommandLineParser.UsageError);
        message.ToError.Should().BeTrue();
        message.Text.ToString().Should().StartWith("monad: ").And.EndWith("Run 'monad --help' for usage.\n");
    }

    [Fact]
    public void Parse_AMissingExplicitConfigFile_IsAMistake()
    {
        CommandLineResult result = CommandLineParser.Parse(
            ["--config", "nowhere.json"], _ => null, ConfigFile.Discover);

        result.Message!.ExitCode.Should().Be(CommandLineParser.UsageError);
        result.Error.Should().Contain("nowhere.json");
    }

    [Fact]
    public void Parse_AMissingExplicitConfigDirectory_IsAMistake()
    {
        CommandLineResult result = CommandLineParser.Parse(
            ["--config", "missing/config.json"], _ => null, _ => throw new DirectoryNotFoundException("missing/config.json"));

        result.Message!.ExitCode.Should().Be(CommandLineParser.UsageError);
        result.Error.Should().Contain("missing/config.json");
    }

    [Theory]
    [InlineData(new[] { "--no-color", "-h" }, true)]
    [InlineData(new[] { "update", "--no-color" }, true)]
    [InlineData(new[] { "--no-color=true", "help" }, true)]
    [InlineData(new[] { "help", "--no-color=true" }, true)]
    [InlineData(new[] { "--no-color=false", "help" }, false)]
    [InlineData(new[] { "--no-color", "false", "help" }, false)]
    [InlineData(new[] { "--", "--no-color" }, false)]
    [InlineData(new[] { "-v" }, false)]
    public void Parse_UsesTheLibrarysColorFlagValue(string[] args, bool expected)
    {
        Parse(args).NoColor.Should().Be(expected);
    }

    [Fact]
    public void Parse_WithAnInvalidColorValue_ReportsAMistake()
    {
        Shown(["--no-color=maybe"]).ExitCode.Should().Be(CommandLineParser.UsageError);
    }

    [Fact]
    public void Parse_AcceptsNoColorAnywhere()
    {
        Shown(["--no-color", "help"]).ExitCode.Should().Be(0);
        Shown(["help", "--no-color"]).ExitCode.Should().Be(0);
        Ok(["update", "--no-color"]).Command.Should().Be(MonadCommand.Update);
        Ok(["--no-color", "run"]).Command.Should().Be(MonadCommand.Run);
    }
}
