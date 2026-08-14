using System;
using System.Collections.Generic;

using FluentAssertions;

using Monad;
using Monad.Audio.Sources;
using Monad.Cli;

using Xunit;

namespace Monad.Tests.Cli;

public sealed class CommandLineParserTests
{
    private static ParseResult Parse(
        string[] args, ConfigFile? file = null, Dictionary<string, string?>? environment = null) =>
        CommandLineParser.Parse(
            args,
            name => environment is not null && environment.TryGetValue(name, out string? value) ? value : null,
            _ => (file ?? ConfigFile.Empty, file is null ? null : "test-config.json"));

    private static MonadOptions Ok(string[] args, ConfigFile? file = null, Dictionary<string, string?>? env = null)
    {
        ParseResult result = Parse(args, file, env);
        result.Error.Should().BeNull();
        return result.Options!;
    }

    // --- Commands ---

    [Fact]
    public void Parse_WithNoArguments_Monitors()
    {
        Ok([]).Command.Should().Be(MonadCommand.Run);
    }

    [Theory]
    [InlineData("run", MonadCommand.Run)]
    [InlineData("devices", MonadCommand.Devices)]
    [InlineData("help", MonadCommand.Help)]
    [InlineData("version", MonadCommand.Version)]
    public void Parse_RecognisesEachCommand(string arg, MonadCommand expected)
    {
        Ok([arg]).Command.Should().Be(expected);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_WithAHelpFlag_ShowsHelp(string flag)
    {
        Ok([flag]).Command.Should().Be(MonadCommand.Help);
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

    [Fact]
    public void Parse_WithTuningThatCannotWork_Fails()
    {
        // Arrange — five samples 60s apart cannot fit in the default 120s window
        ParseResult result = Parse(["run", "--interval", "60"]);

        // Assert
        result.Error.Should().Contain("ringBufferMaxAgeSeconds");
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
        Parse(["run"], file: new ConfigFile { Source = "gramophone" }).Error.Should().Contain("gramophone");
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

    [Fact]
    public void Usage_MentionsEveryCommand()
    {
        CommandLineParser.Usage.Should()
            .Contain("run").And.Contain("devices").And.Contain("replay").And.Contain("--no-recognize");
    }
}
