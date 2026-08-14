using Monad.Audio.Sources;

namespace Monad.Cli;

public enum MonadCommand
{
    Run = 0,
    Devices = 1,
    Replay = 2,
    Help = 3,
    Version = 4,
}

/// <summary>Everything the command line and config file resolved to.</summary>
public sealed record class MonadOptions
{
    public MonadCommand Command { get; init; } = MonadCommand.Run;

    public string? Token { get; init; }

    public string? ConfigPath { get; init; }

    public string? JsonlPath { get; init; }

    /// <summary>The WAV file for <see cref="MonadCommand.Replay"/>.</summary>
    public string? InputPath { get; init; }

    public AudioSourceKind Source { get; init; } = AudioSourceKind.Auto;

    /// <summary>Skip the recognition service entirely, for testing capture and levels.</summary>
    public bool NoRecognize { get; init; }

    public bool Verbose { get; init; }

    public bool Quiet { get; init; }

    public MonadConfig Config { get; init; } = new();
}

/// <summary>
/// Tuning values named on the command line. Kept separate from
/// <see cref="MonadConfig"/> so that "not mentioned" stays distinct from
/// "set to the default", which is what lets the config file fill the gaps.
/// </summary>
public sealed record class TuningOverrides
{
    public int? WindowSeconds { get; init; }

    public int? IntervalSeconds { get; init; }

    public int? MinSegmentDurationSeconds { get; init; }

    public MonadConfig ApplyTo(MonadConfig config) => config with
    {
        WindowSeconds = this.WindowSeconds ?? config.WindowSeconds,
        IntervalSeconds = this.IntervalSeconds ?? config.IntervalSeconds,
        MinSegmentDurationSeconds = this.MinSegmentDurationSeconds ?? config.MinSegmentDurationSeconds,
    };
}
