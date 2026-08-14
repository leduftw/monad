# Monad

[![CI](https://github.com/leduftw/monad/actions/workflows/ci.yml/badge.svg)](https://github.com/leduftw/monad/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/leduftw/monad)](https://github.com/leduftw/monad/releases/latest)

Real-time music recognition monitor. Listens to whatever your machine is
playing, identifies the tracks, and writes out a timeline of what played when.

Runs on macOS, Windows, and Linux, with no virtual audio device to install on
any of them.

```
SEGMENT  [2026-01-15 01:26:01 - 01:27:46]    1m45s  Isabelle Antena - Le Poisson Des Mers Du Sud [ISRC: GB5EM1001054]
SEGMENT  [2026-01-15 01:27:46 - 01:30:42]    2m56s  Eartha Kitt - Je Cherche Un Homme [ISRC: USRC19900468]
SEGMENT  [2026-01-15 01:30:42 - 01:31:52]    1m10s  UNKNOWN
SEGMENT  [2026-01-15 01:31:52 - 01:34:29]    2m37s  Serge Gainsbourg - Couleur Café [ISRC: FRZ036400450]
SEGMENT  [2026-01-15 01:34:29 - 01:36:03]    1m34s  SILENCE
```

## Install

Release binaries contain the .NET runtime, and the macOS archive also contains
the native capture helper. Installing from a release needs neither .NET nor
Xcode.

### Homebrew (macOS and Linux)

```sh
brew install leduftw/tap/monad
```

On Linux the formula also installs PulseAudio's `parec` capture client. It works
with PulseAudio and with PipeWire's PulseAudio compatibility layer.

### WinGet (Windows)

```powershell
winget install leduftw.monad
```

WinGet publication goes through Microsoft's moderated repository. If the first
submission is still being reviewed, use the PowerShell installer below. Once
the package and its release credential are in place, later releases update it
automatically.

### Direct installer

macOS or Linux:

```sh
curl -fsSL https://github.com/leduftw/monad/releases/latest/download/monad-installer.sh | sh
```

Windows PowerShell:

```powershell
irm https://github.com/leduftw/monad/releases/latest/download/monad-installer.ps1 | iex
```

Both installers detect the machine architecture, download the latest GitHub
Release and verify it against that release's `SHA256SUMS`. Set `MONAD_VERSION`
to install a specific version or `MONAD_INSTALL_DIR` to choose the destination.
The Unix installer never invokes `sudo`; if its destination is not already on
`PATH`, it prints the exact line to add.

Versioned copies of both installer scripts are attached to every GitHub
Release for inspection and reproducible use.

### Start monitoring

Get an API token from [AudD](https://audd.io), then:

```sh
export AUDD_API_TOKEN="your-token-here"   # PowerShell: $env:AUDD_API_TOKEN = "your-token-here"
monad
```

Press Ctrl+C to stop. The segment in progress is reported before Monad exits.

To update, use `brew upgrade monad`, `winget upgrade leduftw.monad`, or rerun
the direct installer. All six platform archives are also available from the
[latest GitHub Release](https://github.com/leduftw/monad/releases/latest).

## How it works

Capture runs continuously in the background. Every so often the most recent
window of audio is sliced off, measured, and — only if there is actually
something to hear — sent to [AudD](https://audd.io) to be identified. Results
feed a recency-weighted vote, which smooths over the odd wrong or missing
answer, and a state machine turns that into segments.

```
 System audio (continuous capture)
      |
      v
 ISystemAudioSource      macOS: Core Audio process tap (via a small Swift helper)
      |                  Windows: WASAPI loopback, in process
      |                  Linux: PulseAudio/PipeWire monitor of the default sink
      v
 RollingAudioBuffer      Downmix to mono, keep the last N seconds, pad real
      |                  silence into any stretch the OS delivered nothing for
      v
 WindowAnalyzer          Measure dBFS, skip the lookup when it is silent,
      |                  otherwise boost a copy and encode it as WAV
      v
 AuddRecognizer          Identify the clip; retry transient failures
      |
      v
 SongKey                 Stable identifier, preferring ISRC over artist/title
      |
      v
 WeightedLeaderElection  Recency-weighted vote across recent windows
      |
      v
 SegmentTracker          Song / Silence / Unknown, with hysteresis
      |
      v
 Console + JSON Lines
```

## Platform support

| Platform | Capture path | Needs |
| --- | --- | --- |
| macOS 14.2+ | Core Audio process tap | audio recording permission |
| Windows | WASAPI loopback | nothing extra |
| Linux (glibc) | `parec`, falling back to `ffmpeg` | `pulseaudio-utils` or `ffmpeg` |

On macOS the tap is created against a private aggregate device, so no BlackHole
or Loopback install is needed, and switching output device mid-session (speakers
to AirPods, say) is handled without restarting.

> **macOS permission.** The first run needs permission to record system audio.
> If nothing is captured while something is clearly playing, grant it under
> **System Settings → Privacy & Security → Screen & System Audio Recording**,
> for the terminal you are running Monad from.

> **Linux is untested.** The code path is there and is straightforward, but it
> has not been run on a Linux machine.

The prebuilt Linux archives target glibc. The direct installer detects
musl-based distributions such as Alpine and exits with a clear error instead
of installing an incompatible binary; building from source remains available.

## Build from source

```bash
git clone https://github.com/leduftw/monad.git
cd monad

dotnet build
dotnet run --project Monad
```

On macOS `dotnet build` also compiles the capture helper (`monad-audiotap`) with
`swiftc` and drops it next to the application. If the Xcode command line tools
are missing the build warns rather than failing; install them with
`xcode-select --install`.

<details>
<summary>Running the built binary directly on a Homebrew .NET install</summary>

Homebrew puts the .NET runtime somewhere the generated launcher does not look,
so `./Monad/bin/Debug/net10.0/monad` reports "You must install .NET to run this
application". Point it at the right place:

```bash
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
```

`dotnet run --project Monad` works either way.
</details>

## Usage

```
monad [run] [options]           Monitor system audio (default)
monad devices                   Show the capture path and available devices
monad replay <file.wav>         Run a recording through the same analysis
monad help | version
```

| Option | Meaning |
| --- | --- |
| `--token <token>` | AudD API token (or set `AUDD_API_TOKEN`) |
| `--config <path>` | Config file; defaults to `./monad.json`, then `~/.config/monad/config.json` |
| `--jsonl <path>` | Append every finished segment to a JSON Lines file |
| `--source <name>` | `auto` (default), `macos-tap`, `wasapi`, `pulse` |
| `--window <seconds>` | Audio analysed per lookup (default 12) |
| `--interval <seconds>` | Seconds between lookups (default 15) |
| `--min-segment <secs>` | Drop segments shorter than this (default 30) |
| `--no-recognize` | Skip the API entirely; capture and levels only |
| `-v, --verbose` | Per-window detail |
| `-q, --quiet` | Segments only |

`--interval` is the main lever on cost: each tick is at most one API call, and
silent windows cost nothing at all.

### Keeping a log

```bash
monad --jsonl ~/listening.jsonl
```

One object per line, appended as each segment finishes:

```json
{"kind":"song","startUtc":"2026-01-15T01:26:01.0000000Z","endUtc":"2026-01-15T01:27:46.0000000Z","durationSeconds":105,"songKey":"isrc:GB5EM1001054","display":"Isabelle Antena - Le Poisson Des Mers Du Sud [ISRC: GB5EM1001054]"}
```

### Trying things without spending quota

`--no-recognize` captures and measures but never calls the API, which is the
quickest way to check that capture works on a new machine:

```bash
monad --no-recognize --window 3 --interval 3 -v
```

`monad replay` runs a WAV file through exactly the same analysis on a synthetic
clock, which is useful for tuning thresholds against a known recording:

```bash
monad replay recording.wav --no-recognize --window 4 --interval 4 --min-segment 0
```

### Config file

```bash
# From a source checkout or an extracted release archive:
cp monad.example.json monad.json      # then edit it
```

`monad.json` is gitignored, since it can hold your API token. Anything on the
command line wins over it; `AUDD_API_TOKEN` sits between the two.

```json
{
  "token": "your-token-here",
  "jsonl": "~/listening.jsonl",
  "source": "auto",
  "tuning": {
    "windowSeconds": 12,
    "intervalSeconds": 15,
    "minSegmentDurationSeconds": 30,
    "minLeaderShare": 0.6
  }
}
```

Comments and trailing commas are allowed. A leading `~` in a path is expanded
to your home directory — the shell does that for command line arguments, but a
config file never passes through one.

Every field of `MonadConfig` can be set under `tuning`, by its own name. The
configuration is validated at startup, so a combination that cannot work — vote
weights that do not match the window size, a silence threshold with no
hysteresis — is reported before anything runs.

## Key concepts

### Weighted leader election

Each result joins a short ring buffer, newer entries weighted more heavily than
older ones. Votes are totalled per song key, and the leader has to clear a share
threshold before it counts. Unrecognised windows still count towards the
denominator, so a track has to beat the silence as well as the other guesses.
One wrong answer cannot outvote several consistent ones.

### Segment tracking

Three states, each with hysteresis on entering and leaving:

- **Song** — a track led the vote convincingly, for several windows running.
- **Silence** — the level stayed below the threshold. Silence is measured on the
  raw audio and detected before any lookup is made, so quiet costs nothing.
- **Unknown** — audio is playing but cannot be pinned to one track: ads, speech,
  talk over music.

Segments are dated from when the track was first heard, not from when the vote
settled, so start times line up with the music rather than with the algorithm.
Consecutive segments share a boundary instant, leaving no gaps in the timeline.

### ISRC-based song keys

Tracks are keyed by ISRC where one is available, falling back to
`ARTIST|TITLE`. ISRC is stable: the same recording carries the same code however
its title is spelled, punctuated or transliterated. Codes are collected from the
response itself, from Spotify and from Apple Music; when they disagree, the
disagreement is reported rather than hidden.

## Development

```bash
dotnet build                          # builds the Swift helper too, on macOS
dotnet test
dotnet test --filter "FullyQualifiedName~SegmentTrackerTests"
```

The macOS helper can be exercised on its own:

```bash
./Monad/bin/Debug/net10.0/monad-audiotap probe      # is capture working?
./Monad/bin/Debug/net10.0/monad-audiotap devices    # output devices, as JSON
```

Set `MONAD_AUDIOTAP_PATH` to point at a helper built somewhere else.

Release packaging and downstream Homebrew/WinGet publication are documented in
[`RELEASING.md`](RELEASING.md).

## Licence

MIT. See [LICENSE](LICENSE).
