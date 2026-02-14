# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                          # Build everything
dotnet run --project Monad            # Run (requires AUDD_API_TOKEN env var)
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"  # Run a single test
```

## Architecture

Monad is a real-time music recognition monitor. It captures Windows system audio, identifies songs via the AudD API, and emits timestamped segments.

The pipeline runs as a single-threaded loop in `MonadApp`:

```
LoopbackRecorder (WASAPI) → AudioProcessing (downmix/normalize/RMS)
→ WavEncoder → AuddRecognizer (api.audd.io) → SongKey
→ SegmentTracker (weighted voting + state machine) → Console
```

Three modules, each in its own folder:

- **Audio/** -- Capture and signal processing. `LoopbackRecorder` captures float32 via WASAPI loopback, `AudioProcessing` has static methods for downmix/normalize/RMS, `WavEncoder` encodes PCM16 WAV.
- **Recognition/** -- AudD API client and result parsing. `SongKey` generates stable identifiers preferring ISRC over artist/title text. `IsrcExtractor` pulls ISRC from multiple JSON sources (top-level, Spotify, Apple Music).
- **Aggregation/** -- `WeightedLeaderElection` maintains a ring buffer of recent results with recency-weighted voting. `SegmentTracker` is a state machine (Song/Silence/Unknown) with hysteresis to prevent oscillation.

`MonadConfig` holds all tunable parameters with defaults.

## Conventions

- **Data types:** `sealed record class` for DTOs, `readonly record struct` for lightweight value types.
- **Static utility classes** for stateless operations (AudioProcessing, WavEncoder, SongKey, IsrcExtractor).
- **Primary constructors** on classes that take dependencies.
- **Nullable reference types** are enabled.
- **System.Text.Json** with `TryGetProperty` + `ValueKind` checking (not Newtonsoft).
- **String comparisons:** `StringComparer.OrdinalIgnoreCase` for song keys, `.ToUpperInvariant()` for ISRC.
- **Tests** mirror the source folder structure. xUnit `[Fact]` with FluentAssertions. Method naming: `MethodName_Condition_Expected`. Use `BeApproximately()` for float comparisons.
