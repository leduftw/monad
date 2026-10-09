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
- **Release binaries are only ad-hoc signed on macOS and unsigned on Windows.**
  Homebrew and the one-line installers don't trip Gatekeeper the way a browser
  download does. Developer ID signing would need hardened runtime with
  `com.apple.security.cs.allow-jit` and notarization; never re-sign the .NET
  single-file host with generic options, because that breaks its bundle. The
  playbook only ad-hoc signs binaries that aren't signed yet (the Swift helper).
- **`scripts/smoke-posix-signals.sh` is part of every release.** The playbook runs
  it against the packaged binary on macOS and Linux (see `.github/playbook.toml`),
  so a release can't ship if SIGTERM stops shutting monad down gracefully.

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

<!-- playbook:begin -->
## How work lands here

> Managed by [leduftw/playbook](https://github.com/leduftw/playbook) v1. Change it there, not here; `playbook sync` brings this section back in line.

The facts about this repo live in `.github/playbook.toml`. Everything in this section is the same in every repo that follows the playbook.

### Every change

1. **Start from an issue.** Reuse the issue that describes the change, or create one assigned to `leduftw` with at least one label.
2. **Work in your own worktree.** Run `playbook start <issue>`: it creates `~/Developer/.worktrees/monad/<issue>-<slug>` on branch `dev/leduftw/<issue>-<slug>`, based on the latest `main`. Work only there. Another session may be working at the same time, so never edit the primary checkout; it stays on `main` and only moves with `git pull`.
3. **Commit and push** each finished slice to that branch, without pausing for confirmation.
4. **Open the PR** with `gh pr create --base main`. Its title becomes the commit on `main`, so it follows the commit style below. Put `Closes #<issue>` in the body when the PR fully resolves the issue.
5. **Land it with `playbook finish`.** It waits for the required checks, merges `main` into the branch if `main` has moved on, squash-merges, removes the worktree and the branch, pulls `main`, and confirms the issue is closed. If a check fails, fix it on the branch and run `playbook finish` again.

Sessions that only read, plan or answer questions need no issue and no worktree.

**Never** commit or push to `main` (hooks and a GitHub ruleset refuse it), rebase a branch that's already pushed, or force-push. To catch up with `main`, merge it into your branch.

**Commit style:** start with a lowercase verb that says what the change does, then plain words, with no `feat:`-style prefix and no full stop. Names keep their capitals: `fix Windows installer replacement`, `add opt-in global leaderboard`. The `playbook / title` check rejects PR titles that break this.

**Without the `playbook` command** (a cloud session, a fresh machine), do the same by hand:

- start: `git fetch origin`, then `git worktree add -b dev/leduftw/<issue>-<slug> ~/Developer/.worktrees/monad/<issue>-<slug> origin/main` (a cloud session is already isolated, so a branch from `origin/main` is enough)
- finish: `gh pr checks --watch --required`, then `gh pr merge --squash`; once the PR shows `MERGED`, `git worktree remove <path>`, `git branch -D <branch>` (`-d` refuses, because squash commits aren't ancestors of `main`) and `git pull --ff-only` in the primary checkout

### Releasing

This repo is published: other people install it.

- `playbook release patch|minor|major` opens a PR that only bumps the version; nobody types a version number. The first release is always 1.0.0, and every later one is exactly the next patch, minor or major.
- That PR builds and smoke-tests every release file on all six platforms. Landing it with `playbook finish` tags the release, publishes it on GitHub Releases and updates the Homebrew tap, WinGet and the one-line installers.
- A published release is locked. A broken one is never fixed in place; ship the next patch.
<!-- playbook:end -->
