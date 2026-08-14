using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace Monad.Audio.Sources;

public enum AudioSourceKind
{
    /// <summary>Pick the right capture path for the current operating system.</summary>
    Auto = 0,

    /// <summary>macOS Core Audio process tap, via the bundled sidecar.</summary>
    MacTap = 1,

    /// <summary>Windows WASAPI loopback.</summary>
    Wasapi = 2,

    /// <summary>Linux PulseAudio/PipeWire monitor of the default sink.</summary>
    PulseMonitor = 3,
}

/// <summary>Builds the capture path appropriate to this machine.</summary>
public static class AudioSourceFactory
{
    /// <summary>Overrides where the macOS sidecar is looked up, mostly for development.</summary>
    public const string SidecarPathVariable = "MONAD_AUDIOTAP_PATH";

    public const string SidecarFileName = "monad-audiotap";

    public static ISystemAudioSource Create(AudioSourceKind kind, Action<string> logDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(logDiagnostic);

        return Resolve(kind) switch
        {
            AudioSourceKind.MacTap => CreateMacTap(logDiagnostic),
            AudioSourceKind.Wasapi => CreateWasapi(logDiagnostic),
            AudioSourceKind.PulseMonitor => CreatePulseMonitor(logDiagnostic),
            _ => throw new PlatformNotSupportedException(UnsupportedPlatformMessage),
        };
    }

    public static AudioSourceKind Resolve(AudioSourceKind kind)
    {
        if (kind != AudioSourceKind.Auto)
        {
            return kind;
        }

        if (OperatingSystem.IsMacOS())
        {
            return AudioSourceKind.MacTap;
        }

        if (OperatingSystem.IsWindows())
        {
            return AudioSourceKind.Wasapi;
        }

        return OperatingSystem.IsLinux()
            ? AudioSourceKind.PulseMonitor
            : throw new PlatformNotSupportedException(UnsupportedPlatformMessage);
    }

    private static string UnsupportedPlatformMessage =>
        $"Monad has no system audio capture path for {RuntimeInformationDescription}. "
        + "Supported: macOS 14.2+, Windows, and Linux with PulseAudio or PipeWire.";

    private static string RuntimeInformationDescription =>
        System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    // MARK: macOS

    private static ISystemAudioSource CreateMacTap(Action<string> logDiagnostic)
    {
        string sidecar = LocateSidecar();

        return new ExternalProcessAudioSource(
            description: "Core Audio process tap (macOS system audio)",
            startInfoFactory: () =>
            {
                ProcessStartInfo info = new(sidecar);
                info.ArgumentList.Add("capture");
                return info;
            },
            declaredFormat: null, // the sidecar announces its own format
            logDiagnostic: logDiagnostic);
    }

    /// <summary>
    /// Finds the capture sidecar: an explicit override first, then next to the
    /// application, then whatever is on PATH.
    /// </summary>
    public static string LocateSidecar()
    {
        string? overridePath = Environment.GetEnvironmentVariable(SidecarPathVariable);

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath)
                ? overridePath
                : throw new FileNotFoundException(
                    $"{SidecarPathVariable} points at '{overridePath}', which does not exist.", overridePath);
        }

        string beside = Path.Combine(AppContext.BaseDirectory, SidecarFileName);

        if (File.Exists(beside))
        {
            return beside;
        }

        string? onPath = FindOnPath(SidecarFileName);

        return onPath ?? throw new FileNotFoundException(
            $"Could not find '{SidecarFileName}'. It is built automatically by 'dotnet build' on macOS "
            + $"(requires the Xcode command line tools); set {SidecarPathVariable} to override.",
            beside);
    }

    // MARK: Windows

    private static ISystemAudioSource CreateWasapi(Action<string> logDiagnostic)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI loopback capture is only available on Windows.");
        }

        return CreateWasapiCore(logDiagnostic);
    }

    [SupportedOSPlatform("windows")]
    private static ISystemAudioSource CreateWasapiCore(Action<string> logDiagnostic) =>
        new WasapiLoopbackSource(logDiagnostic);

    // MARK: Linux

    /// <summary>
    /// Captures the default sink's monitor, which is what PulseAudio and
    /// PipeWire expose for "what the machine is playing". Prefers
    /// <c>parec</c>; falls back to ffmpeg's pulse input.
    /// </summary>
    private static ISystemAudioSource CreatePulseMonitor(Action<string> logDiagnostic)
    {
        AudioFormat format = new(48000, 2);

        if (FindOnPath("parec") is { } parec)
        {
            return new ExternalProcessAudioSource(
                description: "PulseAudio monitor of the default sink (parec)",
                startInfoFactory: () =>
                {
                    ProcessStartInfo info = new(parec);
                    info.ArgumentList.Add("--format=float32le");
                    info.ArgumentList.Add($"--rate={format.SampleRate}");
                    info.ArgumentList.Add($"--channels={format.Channels}");
                    info.ArgumentList.Add("--device=@DEFAULT_MONITOR@");
                    info.ArgumentList.Add("--latency-msec=100");
                    return info;
                },
                declaredFormat: format,
                logDiagnostic: logDiagnostic);
        }

        if (FindOnPath("ffmpeg") is { } ffmpeg)
        {
            return new ExternalProcessAudioSource(
                description: "PulseAudio monitor of the default sink (ffmpeg)",
                startInfoFactory: () =>
                {
                    ProcessStartInfo info = new(ffmpeg);
                    info.ArgumentList.Add("-hide_banner");
                    info.ArgumentList.Add("-loglevel");
                    info.ArgumentList.Add("error");
                    info.ArgumentList.Add("-f");
                    info.ArgumentList.Add("pulse");
                    info.ArgumentList.Add("-i");
                    info.ArgumentList.Add("default.monitor");
                    info.ArgumentList.Add("-f");
                    info.ArgumentList.Add("f32le");
                    info.ArgumentList.Add("-ar");
                    info.ArgumentList.Add(format.SampleRate.ToString());
                    info.ArgumentList.Add("-ac");
                    info.ArgumentList.Add(format.Channels.ToString());
                    info.ArgumentList.Add("-");
                    return info;
                },
                declaredFormat: format,
                logDiagnostic: logDiagnostic);
        }

        throw new FileNotFoundException(
            "Linux capture needs either 'parec' (pulseaudio-utils) or 'ffmpeg' on PATH.");
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, fileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
