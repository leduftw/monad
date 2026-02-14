# Monad

Real-time music recognition monitor for Windows. Captures system audio via WASAPI loopback, identifies songs through the AudD API, and tracks what's playing over time as a sequence of segments.

## How it works

Monad runs a continuous loop: record a short clip of system audio, send it to AudD for identification, and feed the result into a weighted voting system that smooths out noisy matches. When the current song changes (or audio goes silent), it emits a timestamped segment.

```
 System Audio
      |
      v
 LoopbackRecorder          Capture audio via WASAPI loopback
      |
      v
 AudioProcessing            Downmix to mono, normalize, measure dBFS
      |
      v
 WavEncoder                 Encode as WAV for API submission
      |
      v
 AuddRecognizer             Send to AudD, parse response
      |
      v
 SongKey                    Generate stable identifier (ISRC or artist/title)
      |
      v
 SegmentTracker             Weighted leader election + state machine
      |
      v
 Console Output             Emit timestamped segments on state transitions
```

## Prerequisites

- **Windows** -- WASAPI loopback capture requires it
- **.NET SDK** -- see `Monad.csproj` for the target framework version
- **AudD API token** -- register at [audd.io](https://audd.io)

## Getting started

```bash
git clone https://github.com/leduftw/Harmony.git
cd Harmony

# Set your AudD API token
export AUDD_API_TOKEN="your-token-here"

# Run
dotnet run --project Monad
```

Press Ctrl+C to stop.

## Running tests

```bash
dotnet test
```

## Configuration

All tunable parameters live in `MonadConfig.cs` with sensible defaults. Open that file to see what's available -- it covers sampling intervals, ring buffer sizing, vote weights, leader election thresholds, silence detection, and segment filtering.

## Key concepts

### Weighted leader election

Each recognition result is added to a fixed-size ring buffer. Newer samples get higher weight than older ones. Votes are aggregated per song key, and the key with the highest weighted total becomes the leader -- but only if its share exceeds a configurable threshold. This smooths out transient misidentifications: a single wrong result can't override several correct ones.

### Segment tracking

The segment tracker is a state machine with three states:

- **Song** -- a stable leader was found with sufficient vote share, persisting across multiple loops.
- **Silence** -- audio level dropped below the silence threshold for enough consecutive loops.
- **Unknown** -- audio is present but results are scattered (high no-match rate or too many distinct keys). Covers ads, speech, DJ talk, etc.

When the state changes, the previous segment is finalized and emitted (if it meets the minimum duration).

### ISRC-based song keys

Songs are identified by ISRC (International Standard Recording Code) when available, falling back to an `ARTIST|TITLE` text key. ISRC is preferred because it's a stable identifier -- the same song always has the same ISRC regardless of how the title is formatted or transliterated. The extractor checks multiple sources in AudD responses (top-level, Spotify, Apple Music) and detects mismatches.

### Hysteresis

State transitions use different thresholds for entering and exiting to prevent oscillation on borderline audio levels. All transitions also require persistence -- multiple consecutive qualifying samples before taking effect.
