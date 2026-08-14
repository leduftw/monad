using System;
using System.IO;

using FluentAssertions;

using Monad.Cli;

using Xunit;

namespace Monad.Tests.Cli;

public sealed class ConfigFileTests : IDisposable
{
    private readonly string directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"monad-cfg-{Guid.NewGuid():N}")).FullName;

    private string Write(string json)
    {
        string path = Path.Combine(this.directory, "monad.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    [Fact]
    public void Load_ReadsTopLevelSettings()
    {
        // Arrange
        string path = this.Write("""{"token":"abc","jsonl":"out.jsonl","source":"pulse"}""");

        // Act
        ConfigFile config = ConfigFile.Load(path);

        // Assert
        config.Token.Should().Be("abc");
        config.Jsonl.Should().Be("out.jsonl");
        config.Source.Should().Be("pulse");
    }

    [Fact]
    public void Load_ReadsTuningValues()
    {
        // Arrange
        string path = this.Write("""{"tuning":{"windowSeconds":20,"minLeaderShare":0.75}}""");

        // Act
        ConfigFile config = ConfigFile.Load(path);

        // Assert
        config.Tuning!.WindowSeconds.Should().Be(20);
        config.Tuning.MinLeaderShare.Should().Be(0.75);
    }

    [Fact]
    public void Load_LeavesUnmentionedTuningAtItsDefault()
    {
        // Arrange
        string path = this.Write("""{"tuning":{"windowSeconds":20}}""");

        // Act
        ConfigFile config = ConfigFile.Load(path);

        // Assert
        config.Tuning!.IntervalSeconds.Should().Be(new MonadConfig().IntervalSeconds);
        config.Tuning.VoteWeightsNewestToOldest.Should().Equal(new MonadConfig().VoteWeightsNewestToOldest);
    }

    [Fact]
    public void Load_ReadsAnArrayOfVoteWeights()
    {
        // Arrange
        string path = this.Write("""{"tuning":{"ringBufferMaxSamples":3,"voteWeightsNewestToOldest":[1,0.5,0.25]}}""");

        // Act
        ConfigFile config = ConfigFile.Load(path);

        // Assert
        config.Tuning!.VoteWeightsNewestToOldest.Should().Equal(1.0, 0.5, 0.25);
    }

    [Fact]
    public void Load_IgnoresPropertyCase()
    {
        this.Write("""{"Token":"abc","TUNING":{"WindowSeconds":9}}""");

        ConfigFile config = ConfigFile.Load(Path.Combine(this.directory, "monad.json"));

        config.Token.Should().Be("abc");
        config.Tuning!.WindowSeconds.Should().Be(9);
    }

    [Fact]
    public void Load_AllowsCommentsAndTrailingCommas()
    {
        // Arrange — config files get hand-edited, so be forgiving
        string path = this.Write("""
            {
              // which token to use
              "token": "abc",
            }
            """);

        // Act / Assert
        ConfigFile.Load(path).Token.Should().Be("abc");
    }

    [Fact]
    public void Load_WithInvalidJson_ExplainsWhy()
    {
        // Arrange
        string path = this.Write("{ not json at all");

        // Act / Assert
        FluentActions.Invoking(() => ConfigFile.Load(path))
            .Should().Throw<InvalidDataException>()
            .WithMessage($"*{path}*");
    }

    [Fact]
    public void Load_WithAnEmptyObject_ReturnsDefaults()
    {
        // Arrange
        string path = this.Write("{}");

        // Act
        ConfigFile config = ConfigFile.Load(path);

        // Assert
        config.Token.Should().BeNull();
        config.Tuning.Should().BeNull();
    }

    // --- Discovery ---

    [Fact]
    public void Discover_WithAnExplicitPath_LoadsIt()
    {
        // Arrange
        string path = this.Write("""{"token":"explicit"}""");

        // Act
        (ConfigFile config, string? from) = ConfigFile.Discover(path);

        // Assert
        config.Token.Should().Be("explicit");
        from.Should().Be(path);
    }

    [Fact]
    public void Discover_WithAnExplicitPathThatIsMissing_Throws()
    {
        FluentActions
            .Invoking(() => ConfigFile.Discover(Path.Combine(this.directory, "nope.json")))
            .Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Discover_WithNothingToFind_ReturnsEmpty()
    {
        // Arrange — point the search at a directory with no config in it
        string? previousHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string previousDirectory = Directory.GetCurrentDirectory();

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", this.directory);
            Directory.SetCurrentDirectory(this.directory);

            // Act
            (ConfigFile config, string? from) = ConfigFile.Discover(null);

            // Assert
            from.Should().BeNull();
            config.Should().BeSameAs(ConfigFile.Empty);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousHome);
        }
    }

    [Fact]
    public void Discover_FindsMonadJsonInTheWorkingDirectory()
    {
        // Arrange
        this.Write("""{"token":"from-cwd"}""");

        string previousDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(this.directory);

            // Act
            (ConfigFile config, string? from) = ConfigFile.Discover(null);

            // Assert
            config.Token.Should().Be("from-cwd");
            from.Should().NotBeNull();
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }
}
