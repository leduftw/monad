using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Monad.Update;

/// <summary>How monad was installed.</summary>
public enum InstallChannel
{
    Homebrew,
    WinGet,

    /// <summary>The one-line installer, which put monad in a folder of its own choosing.</summary>
    Installer,

    /// <summary>Built from source (<c>bin/Debug</c> or <c>bin/Release</c>), so there's nothing to update.</summary>
    LocalBuild,

    /// <summary>Not a monad executable at all, such as <c>dotnet monad.dll</c>.</summary>
    Unknown,
}

/// <summary>The command that updates monad through its channel.</summary>
/// <param name="Manager">What does the update, as the user knows it.</param>
/// <param name="InstallDirectory"><c>MONAD_INSTALL_DIR</c> for the installer, so it replaces this copy.</param>
/// <param name="Detach">Windows can't replace a running program, so monad starts this and exits.</param>
public sealed record class UpdatePlan(
    string Manager,
    string Program,
    IReadOnlyList<string> Arguments,
    string? InstallDirectory,
    bool Detach);

/// <summary>
/// <c>monad update</c>: moves to the latest release through whatever installed
/// monad. The channel comes from where the executable really is, after
/// following links: Homebrew's <c>bin/monad</c> is only a link into its Cellar.
/// </summary>
public static class Updater
{
    private const string LatestRelease = "https://github.com/leduftw/monad/releases/latest";
    private const string InstallerSh = "https://github.com/leduftw/monad/releases/latest/download/monad-installer.sh";
    private const string InstallerPs1 = "https://github.com/leduftw/monad/releases/latest/download/monad-installer.ps1";

    /// <summary>Where a version's WinGet manifest appears once Microsoft has accepted it.</summary>
    private const string WinGetManifests =
        "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/l/leduftw/monad";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Tells the channel from the executable's resolved path.</summary>
    public static InstallChannel ChannelOf(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);

        string path = executable.Replace('\\', '/');
        string name = path[(path.LastIndexOf('/') + 1)..];

        if (name is not ("monad" or "monad.exe"))
        {
            return InstallChannel.Unknown;
        }

        if (path.Contains("/Cellar/monad/", StringComparison.Ordinal))
        {
            return InstallChannel.Homebrew;
        }

        if (path.Contains("/microsoft/winget/", StringComparison.OrdinalIgnoreCase))
        {
            return InstallChannel.WinGet;
        }

        if (path.Contains("/bin/Debug/", StringComparison.Ordinal) || path.Contains("/bin/Release/", StringComparison.Ordinal))
        {
            return InstallChannel.LocalBuild;
        }

        return InstallChannel.Installer;
    }

    /// <summary>What updates monad on this channel; null when nothing can.</summary>
    public static UpdatePlan? PlanFor(InstallChannel channel, string executable, bool windows)
    {
        ArgumentNullException.ThrowIfNull(executable);

        string folder = Path.GetDirectoryName(executable) ?? ".";

        return channel switch
        {
            InstallChannel.Homebrew => new("Homebrew", "brew", ["upgrade", "leduftw/tap/monad"], null, Detach: false),
            InstallChannel.WinGet => new("WinGet", "winget", ["upgrade", "--id", "leduftw.monad"], null, Detach: true),
            InstallChannel.Installer when windows => new(
                "the installer",
                "powershell",
                ["-NoProfile", "-Command", $"irm {InstallerPs1} | iex"],
                folder,
                Detach: true),

            // Download first, so a failed download fails the update instead of
            // piping nothing into `sh`, which would succeed.
            InstallChannel.Installer => new(
                "the installer",
                "sh",
                ["-c", $"script=$(curl -fsSL {InstallerSh}) && printf '%s\\n' \"$script\" | sh"],
                folder,
                Detach: false),
            _ => null,
        };
    }

    /// <summary>
    /// The version that GitHub's <c>releases/latest</c> redirect points at, from
    /// its Location header, such as <c>https://github.com/leduftw/monad/releases/tag/v2.1.0</c>.
    /// </summary>
    public static Version? VersionFromLocation(string? location)
    {
        const string Marker = "/releases/tag/v";
        int at = location?.LastIndexOf(Marker, StringComparison.Ordinal) ?? -1;

        return at < 0 ? null : ParseVersion(location![(at + Marker.Length)..]);
    }

    /// <summary>A plain <c>major.minor.patch</c>, ignoring build metadata such as <c>+abc123</c>.</summary>
    public static Version? ParseVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int metadata = text.IndexOfAny(['+', '-']);
        string core = metadata < 0 ? text : text[..metadata];
        string[] parts = core.Split('.');

        return parts.Length == 3 && System.Version.TryParse(core, out Version? version) ? version : null;
    }

    /// <summary>Follows links to the file a path really names.</summary>
    public static string Resolve(string path) => Resolve(path, remainingLinks: 64);

    private static string Resolve(string path, int remainingLinks)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string resolved = root;

        foreach (string part in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(resolved, part);
            FileSystemInfo entry = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: false);

            if (target is not null && remainingLinks == 0)
            {
                throw new IOException($"Too many symbolic links while resolving '{path}'.");
            }

            resolved = target is null ? candidate : Resolve(target.FullName, remainingLinks - 1);
        }

        return resolved;
    }

    /// <summary>Runs <c>monad update</c> and returns its exit code.</summary>
    public static async Task<int> RunAsync(string currentVersion, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };
        return await RunAsync(currentVersion, Environment.ProcessPath, http, output, error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the same update with an explicit executable and HTTP transport.</summary>
    public static async Task<int> RunAsync(
        string currentVersion,
        string? executable,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            await UpdateAsync(currentVersion, executable, http, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"monad: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task UpdateAsync(
        string currentVersion,
        string? processPath,
        HttpClient http,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string executable = Resolve(processPath ?? throw new UpdateException("can't tell where monad is installed."));
        InstallChannel channel = ChannelOf(executable);

        if (channel == InstallChannel.LocalBuild)
        {
            throw new UpdateException($"{executable} is a local build; update it with `git pull` and `dotnet build`.");
        }

        UpdatePlan plan = PlanFor(channel, executable, OperatingSystem.IsWindows())
            ?? throw new UpdateException($"can't tell how monad was installed: it runs as {executable}.");
        Version current = ParseVersion(currentVersion)
            ?? throw new UpdateException($"can't read this monad's version, {currentVersion}.");

        http.DefaultRequestHeaders.UserAgent.ParseAdd($"monad/{current}");

        Version latest = await LatestVersionAsync(http, cancellationToken).ConfigureAwait(false);

        if (latest <= current)
        {
            await output.WriteLineAsync(latest == current
                ? $"monad {current} is the latest version."
                : $"monad {current} is newer than the latest release, {latest}.").ConfigureAwait(false);
            return;
        }

        if (channel == InstallChannel.WinGet && !await OnWinGetAsync(http, latest, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(
                $"monad {latest} is out, but not on WinGet yet: Microsoft reviews each new version first, "
                + "which can take a few days.").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"Updating monad {current} to {latest} with {plan.Manager}.").ConfigureAwait(false);

        int exitCode = await StartAsync(plan, latest, cancellationToken).ConfigureAwait(false);

        if (plan.Detach)
        {
            await output.WriteLineAsync($"{plan.Manager} finishes the update in this window once monad has exited.")
                .ConfigureAwait(false);
        }
        else if (exitCode != 0)
        {
            throw new UpdateException($"{plan.Manager} didn't finish the update (exit code {exitCode}).");
        }
    }

    /// <summary>Starts the plan's command; waits for it unless it is detached.</summary>
    public static async Task<int> StartAsync(UpdatePlan plan, Version version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ProcessStartInfo info = new(plan.Program) { UseShellExecute = false };

        foreach (string argument in plan.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (plan.InstallDirectory is { } folder)
        {
            info.Environment["MONAD_INSTALL_DIR"] = folder;

            // An old installation pin must not undo the version check.
            info.Environment["MONAD_VERSION"] = version.ToString();
        }

        Process process;

        try
        {
            process = Process.Start(info) ?? throw new UpdateException($"can't start {plan.Program}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UpdateException($"can't run {plan.Program}: {ex.Message}");
        }

        using (process)
        {
            if (plan.Detach)
            {
                return 0;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    private static async Task<Version> LatestVersionAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(LatestRelease, cancellationToken).ConfigureAwait(false);

            return VersionFromLocation(response.Headers.Location?.ToString())
                ?? throw new UpdateException(
                    $"can't check the latest release: GitHub answered {(int)response.StatusCode} without one.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"can't check the latest release: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"can't check the latest release: {ex.Message}");
        }
    }

    private static async Task<bool> OnWinGetAsync(HttpClient http, Version version, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http
                .GetAsync(new Uri($"{WinGetManifests}/{version}/leduftw.monad.yaml"), cancellationToken)
                .ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => true,
                HttpStatusCode.NotFound => false,
                HttpStatusCode status => throw new UpdateException(
                    $"can't check WinGet for {version}: its manifests answered {(int)status}."),
            };
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"can't check WinGet for {version}: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"can't check WinGet for {version}: {ex.Message}");
        }
    }

    private sealed class UpdateException(string message) : Exception(message);
}
