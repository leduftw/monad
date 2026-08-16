# Agent instructions

This file provides guidance to coding agents when working with code in this repository.

## Commands

```bash
dotnet build                          # Build everything (also compiles the Swift helper on macOS)
dotnet run --project Monad            # Run (requires AUDD_API_TOKEN, or pass --no-recognize)
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"  # Run a single test
dotnet run --project Monad -- devices # Show the resolved capture path
```

Handy while working on capture:

```bash
dotnet run --project Monad -- run --no-recognize -v --window 3 --interval 3
dotnet run --project Monad -- replay clip.wav --no-recognize --min-segment 0
./Monad/bin/Debug/net10.0/monad-audiotap probe
```

## Architecture

Monad captures system audio, identifies what is playing through the AudD API,
and emits a timeline of segments. Capture is **continuous**: a background pump
fills a rolling buffer while a timer slices windows off it.

```
ISystemAudioSource → RollingAudioBuffer → WindowAnalyzer → AuddRecognizer
→ SongKey → WeightedLeaderElection → SegmentTracker → ISegmentSink
```

- **Audio/** — Capture and signal processing. `ISystemAudioSource` has one
  implementation per platform under `Audio/Sources`: `WasapiLoopbackSource`
  (Windows, in process) and `ExternalProcessAudioSource` (macOS and Linux,
  reading raw f32le PCM from a helper's stdout). `RollingAudioBuffer` is a
  wall-clock-aligned mono ring that pads real silence into stretches where the
  OS delivered nothing. `AudioProcessing`, `WavEncoder` and `WavReader` are
  static helpers.
- **Analysis/** — `WindowAnalyzer` measures one window and identifies it;
  `ReplayRunner` drives the same analysis over a WAV file. Both live and replay
  paths share `WindowAnalyzer` so they cannot drift apart.
- **Recognition/** — `ISongRecognizer` and the AudD client. `RecognitionOutcome`
  distinguishes a match, an honest no-match, and a failure — and failures are
  deliberately kept out of the vote. `SongKey` prefers ISRC; `IsrcExtractor`
  gathers codes from the response, Spotify and Apple Music and reports
  disagreement.
- **Aggregation/** — `WeightedLeaderElection` (recency-weighted vote, reporting
  when the leading track was first heard) and `SegmentTracker` (Song / Silence /
  Unknown state machine with hysteresis, plus `Flush` for shutdown).
- **Output/** — `ISegmentSink`: console, JSON Lines, and a composite.
- **Cli/** — Hand-rolled argument parsing and the JSON config file.
- **native/macos/AudioTap/** — Swift sidecar. Core Audio process taps are an
  Objective-C API, so this is a separate binary that streams PCM to stdout,
  built by an MSBuild target in `Monad.csproj`.

`MonadConfig` holds every tunable with defaults, and `Validate()` rejects
combinations that cannot work.

### Things that are the way they are for a reason

- **Levels are measured before the audio is boosted.** `BoostToPeak` exists to
  give the API a healthy signal; measuring after it would push every quiet
  window towards full scale and silence would never be detected.
- **Silent windows never reach the API.** The silence gate runs first, so quiet
  costs nothing.
- **A failed lookup casts no vote.** An outage is not evidence the music
  stopped, so `WindowAnalyzer` returns a null sample rather than a no-match.
- **Capture loops must check their cancellation token.** While audio is flowing
  reads always succeed and never observe cancellation on their own.
- **Signals go through `PosixSignalRegistration`, not `Console.CancelKeyPress`**,
  which covers SIGTERM and does not depend on owning a terminal.
- **The macOS sidecar reports the aggregate device's sample rate, not the tap's.**
  `kAudioTapPropertyFormat` advertises 48 kHz even when the output device runs at
  44.1 kHz. Believing it stretches every clip by 8.8% — audible as nothing at all,
  but enough that recognition never matches anything. Verify with a known tone:
  play a 440 Hz sine, capture it, and check it comes back at 440 Hz.
- **`install.sh` keeps its whole body in `main`, called on the last line.** README
  tells people to pipe it into `sh`, and a shell reading a pipe runs each command
  as it arrives, so a dropped connection would otherwise execute whichever prefix
  had arrived. The indentation is load-bearing: unwrapped, a truncated download
  can install the binary without printing the version or the PATH advice, and can
  leave a temp directory behind if it stops between `mktemp` and the `trap`.
  It is also plain POSIX for the same reason — `/bin/sh` is dash on Debian.

## Conventions

- **Data types:** `sealed record class` for DTOs, `readonly record struct` for lightweight value types.
- **Static utility classes** for stateless operations (AudioProcessing, WavEncoder, SongKey, IsrcExtractor).
- **Primary constructors** on classes that take dependencies.
- **Nullable reference types** are enabled.
- **System.Text.Json** with `TryGetProperty` + `ValueKind` checking (not Newtonsoft). Never call
  `GetString()` without checking `ValueKind` first — a field of the wrong type throws.
- **String comparisons:** `StringComparer.OrdinalIgnoreCase` for song keys, `.ToUpperInvariant()` for ISRC.
- **Tests** mirror the source folder structure. xUnit `[Fact]` with FluentAssertions. Method naming:
  `MethodName_Condition_Expected`. Use `BeApproximately()` for float comparisons. Shared fakes live in
  `Monad.Tests/TestDoubles.cs`. Every test asserts something — a test with no assertion, or one whose
  assertion sits inside an `if`, is worse than no test.
- Keep `AGENTS.md` and `CLAUDE.md` byte-identical. The tracked `.githooks/pre-commit` hook enforces this when `core.hooksPath` is `.githooks`.
