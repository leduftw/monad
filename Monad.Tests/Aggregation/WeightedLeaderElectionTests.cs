using System;
using FluentAssertions;
using Monad.Aggregation;
using Xunit;

namespace Monad.Tests.Aggregation;

public sealed class WeightedLeaderElectionTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WeightedLeaderElection CreateDefault() =>
        new(maxSamples: 3, maxAge: TimeSpan.FromMinutes(5), weightsNewestToOldest: [1.0, 0.5, 0.25]);

    private static RecognitionSample MakeSample(string? songKey, bool isNoMatch, DateTime? timestamp = null) =>
        new(timestamp ?? Now, Dbfs: -20.0, SongKey: songKey, IsNoMatch: isNoMatch);

    // --- Constructor ---

    [Fact]
    public void Constructor_WithWeightsLengthMismatch_ThrowsArgumentException()
    {
        // Arrange / Act
        Action act = () => new WeightedLeaderElection(
            maxSamples: 3, maxAge: TimeSpan.FromMinutes(5), weightsNewestToOldest: [1.0, 0.5]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithMatchingWeightsLength_DoesNotThrow()
    {
        // Arrange / Act
        Action act = () => new WeightedLeaderElection(
            maxSamples: 2, maxAge: TimeSpan.FromMinutes(5), weightsNewestToOldest: [1.0, 0.5]);

        // Assert
        act.Should().NotThrow();
    }

    // --- Add / Ring Buffer ---

    [Fact]
    public void Add_WithMoreThanMaxSamples_EvictsOldest()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:BBB", false));
        election.Add(MakeSample("isrc:CCC", false));
        election.Add(MakeSample("isrc:DDD", false)); // should evict AAA

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(3);
    }

    [Fact]
    public void Add_WithExpiredSamples_PrunesByAge()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        DateTime old = Now.AddMinutes(-10); // expired (maxAge = 5 min)
        election.Add(MakeSample("isrc:OLD", false, old));
        election.Add(MakeSample("isrc:NEW", false, Now)); // pruning happens on add

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(1);
    }

    // --- ComputeLeader ---

    [Fact]
    public void ComputeLeader_WithNoSamples_ReturnsEmpty()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.Should().Be(LeaderSnapshot.Empty);
    }

    [Fact]
    public void ComputeLeader_WithSingleSample_ReturnsThatKeyWithFullShare()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:AAA");
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithUnanimousSamples_ReturnsFullShare()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:AAA", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:AAA");
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithCompetingKeys_RecencyWeightedWinnerWins()
    {
        // Arrange — weights [1.0, 0.5, 0.25] (newest-to-oldest)
        // oldest: AAA (weight 0.25), middle: AAA (weight 0.5), newest: BBB (weight 1.0)
        // AAA total = 0.25 + 0.5 = 0.75, BBB total = 1.0, totalWeight = 1.75
        // BBB wins despite fewer samples because it's newest and gets the highest weight
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:BBB", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:BBB");
        snapshot.LeaderShare.Should().BeApproximately(1.0 / 1.75, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithAllNullKeys_ReturnsNullLeader()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample(null, true));
        election.Add(MakeSample(null, true));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().BeNull();
        snapshot.LeaderShare.Should().Be(0.0);
    }

    [Fact]
    public void ComputeLeader_WithCaseInsensitiveKeys_AggregatesTogether()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:aaa", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().BeOneOf("isrc:AAA", "isrc:aaa");
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_WithFewerSamplesThanMax_UsesHighestWeights()
    {
        // Arrange — 1 sample, weights [1.0, 0.5, 0.25] (newest-to-oldest)
        // Single sample is newest, gets weight 1.0
        // Single key gets 100% share regardless
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert — share should be 1.0 (only one key, gets all weight)
        snapshot.LeaderShare.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ComputeLeader_ShareCalculation_IsAccurate()
    {
        // Arrange — 2 samples, weights [1.0, 0.5] (newest-to-oldest, first 2 used)
        // oldest: AAA (weight 0.5), newest: BBB (weight 1.0)
        // totalWeight = 1.5, BBB share = 1.0/1.5 ≈ 0.6667
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:BBB", false));

        // Act
        LeaderSnapshot snapshot = election.ComputeLeader();

        // Assert
        snapshot.LeaderKey.Should().Be("isrc:BBB");
        snapshot.LeaderShare.Should().BeApproximately(1.0 / 1.5, 0.001);
    }

    // --- ComputeScatterStats ---

    [Fact]
    public void ComputeScatterStats_WithNoSamples_ReturnsEmpty()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.Should().Be(ScatterStats.Empty);
    }

    [Fact]
    public void ComputeScatterStats_WithAllNoMatch_CountsCorrectly()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample(null, true));
        election.Add(MakeSample(null, true));

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(2);
        stats.NoMatchSamples.Should().Be(2);
        stats.DistinctSongKeys.Should().Be(0);
    }

    [Fact]
    public void ComputeScatterStats_WithMixedSamples_CountsDistinctKeysCaseInsensitive()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample("isrc:AAA", false));
        election.Add(MakeSample("isrc:aaa", false)); // same key, different case
        election.Add(MakeSample("isrc:BBB", false));

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.TotalSamples.Should().Be(3);
        stats.NoMatchSamples.Should().Be(0);
        stats.DistinctSongKeys.Should().Be(2);
    }

    [Fact]
    public void ComputeScatterStats_WithNullSongKey_CountedAsNoMatch()
    {
        // Arrange
        WeightedLeaderElection election = CreateDefault();
        election.Add(MakeSample(null, false)); // null key, not explicitly IsNoMatch

        // Act
        ScatterStats stats = election.ComputeScatterStats();

        // Assert
        stats.NoMatchSamples.Should().Be(1);
    }
}
