using System;

using FluentAssertions;

using Monad.Aggregation;

using Xunit;

namespace Monad.Tests.Aggregation;

public sealed class WeightedLeaderElectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WeightedLeaderElection CreateDefault() =>
        new(maxSamples: 3, maxAge: TimeSpan.FromMinutes(5), weightsNewestToOldest: [1.0, 0.5, 0.25]);

    private static RecognitionSample Sample(string? key, DateTimeOffset? at = null) =>
        new(at ?? Now, Dbfs: -20.0, SongKey: key, IsNoMatch: key is null);

    // --- Constructor ---

    [Fact]
    public void Constructor_WithWrongNumberOfWeights_Throws()
    {
        FluentActions
            .Invoking(() => new WeightedLeaderElection(3, TimeSpan.FromMinutes(5), [1.0, 0.5]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNegativeWeight_Throws()
    {
        FluentActions
            .Invoking(() => new WeightedLeaderElection(2, TimeSpan.FromMinutes(5), [1.0, -0.5]))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveSampleCount_Throws(int maxSamples)
    {
        FluentActions
            .Invoking(() => new WeightedLeaderElection(maxSamples, TimeSpan.FromMinutes(5), []))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithMatchingWeights_DoesNotThrow()
    {
        FluentActions
            .Invoking(() => new WeightedLeaderElection(2, TimeSpan.FromMinutes(5), [1.0, 0.5]))
            .Should().NotThrow();
    }

    [Fact]
    public void Constructor_CopiesTheWeights()
    {
        // Arrange — the caller must not be able to change the weights afterwards
        double[] weights = [1.0, 0.5];
        WeightedLeaderElection election = new(2, TimeSpan.FromMinutes(5), weights);

        // Act
        weights[0] = 99.0;
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:BBB"));

        // Assert — BBB is newest and takes weight 1.0, so its share is 1/1.5
        election.ComputeLeader().LeaderShare.Should().BeApproximately(1.0 / 1.5, 0.001);
    }

    // --- Ring buffer ---

    [Fact]
    public void Add_BeyondCapacity_DropsTheOldest()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:BBB"));
        election.Add(Sample("isrc:CCC"));
        election.Add(Sample("isrc:DDD"));

        // Act / Assert
        election.Count.Should().Be(3);
        election.ComputeScatterStats().TotalSamples.Should().Be(3);
    }

    [Fact]
    public void Add_DropsSamplesOlderThanMaxAge()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:OLD", Now.AddMinutes(-10)));

        // Act
        election.Add(Sample("isrc:NEW", Now));

        // Assert
        election.ComputeScatterStats().TotalSamples.Should().Be(1);
    }

    [Fact]
    public void Clear_EmptiesTheWindow()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));

        // Act
        election.Clear();

        // Assert
        election.ComputeLeader().Should().Be(LeaderSnapshot.Empty);
    }

    // --- ComputeLeader ---

    [Fact]
    public void ComputeLeader_WithNoSamples_ReturnsEmpty()
    {
        CreateDefault().ComputeLeader().Should().Be(LeaderSnapshot.Empty);
    }

    [Fact]
    public void ComputeLeader_WithOneSample_GivesItTheWholeShare()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:AAA");
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithUnanimousSamples_GivesFullShare()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:AAA"));

        // Act / Assert
        election.ComputeLeader().LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_LetsRecentResultsOutweighOlderOnes()
    {
        // Arrange — weights newest-to-oldest are [1.0, 0.5, 0.25]:
        // AAA collects 0.25 + 0.5 = 0.75, BBB collects 1.0 for being newest.
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:BBB"));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:BBB");
        snapshot.LeaderShare.Should().BeApproximately(1.0 / 1.75, 0.001);
    }

    [Fact]
    public void ComputeLeader_CountsUnrecognisedWindowsAgainstTheShare()
    {
        // Arrange — a track has to beat the silence too, not just rival guesses
        WeightedLeaderElection election = new(2, TimeSpan.FromMinutes(5), [1.0, 1.0]);
        election.Add(Sample(null));
        election.Add(Sample("isrc:AAA"));

        // Act / Assert
        election.ComputeLeader().LeaderShare.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithNothingRecognised_ReturnsEmpty()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample(null));
        election.Add(Sample(null));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().BeNull();
        snapshot.LeaderShare.Should().Be(0.0);
    }

    [Fact]
    public void ComputeLeader_TreatsKeysCaseInsensitively()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:aaa"));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().BeOneOf("isrc:AAA", "isrc:aaa");
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithAPartlyFilledWindow_UsesTheHeaviestWeights()
    {
        // Arrange — two samples use weights [1.0, 0.5], so total weight is 1.5
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:BBB"));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:BBB");
        snapshot.LeaderShare.Should().BeApproximately(1.0 / 1.5, 0.001);
    }

    // --- LeaderSinceUtc ---

    [Fact]
    public void ComputeLeader_ReportsWhenTheLeadingTrackWasFirstHeard()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA", Now));
        election.Add(Sample("isrc:AAA", Now.AddSeconds(15)));
        election.Add(Sample("isrc:AAA", Now.AddSeconds(30)));

        // Act / Assert
        election.ComputeLeader().LeaderSinceUtc.Should().Be(Now);
    }

    [Fact]
    public void ComputeLeader_DoesNotLetAnEarlierTrackExtendTheCurrentRun()
    {
        // Arrange — BBB took over, so its run starts after AAA's last sighting
        WeightedLeaderElection election = new(3, TimeSpan.FromMinutes(5), [1.0, 1.0, 0.1]);
        election.Add(Sample("isrc:AAA", Now));
        election.Add(Sample("isrc:BBB", Now.AddSeconds(15)));
        election.Add(Sample("isrc:BBB", Now.AddSeconds(30)));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:BBB");
        snapshot.LeaderSinceUtc.Should().Be(Now.AddSeconds(15));
    }

    [Fact]
    public void ComputeLeader_TreatsAnUnrecognisedWindowAsPartOfTheSameRun()
    {
        // Arrange — a failed lookup mid-song must not restart the segment
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA", Now));
        election.Add(Sample(null, Now.AddSeconds(15)));
        election.Add(Sample("isrc:AAA", Now.AddSeconds(30)));

        // Act / Assert
        election.ComputeLeader().LeaderSinceUtc.Should().Be(Now);
    }

    // --- ComputeScatterStats ---

    [Fact]
    public void ComputeScatterStats_WithNoSamples_ReturnsEmpty()
    {
        CreateDefault().ComputeScatterStats().Should().Be(ScatterStats.Empty);
    }

    [Fact]
    public void ComputeScatterStats_CountsUnrecognisedWindows()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample(null));
        election.Add(Sample(null));

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(2);
        stats.NoMatchSamples.Should().Be(2);
        stats.DistinctSongKeys.Should().Be(0);
        stats.NoMatchShare.Should().Be(1.0);
    }

    [Fact]
    public void ComputeScatterStats_CountsDistinctKeysCaseInsensitively()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(Sample("isrc:AAA"));
        election.Add(Sample("isrc:aaa"));
        election.Add(Sample("isrc:BBB"));

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(3);
        stats.NoMatchSamples.Should().Be(0);
        stats.DistinctSongKeys.Should().Be(2);
    }

    [Fact]
    public void NoMatchShare_WithNoSamples_IsTreatedAsFullyUnrecognised()
    {
        ScatterStats.Empty.NoMatchShare.Should().Be(1.0);
    }
}
