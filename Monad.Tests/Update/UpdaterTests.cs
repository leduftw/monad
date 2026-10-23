using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Update;

using Xunit;

namespace Monad.Tests.Update;

public sealed class UpdaterTests
{
    // --- Channels ---

    [Theory]
    [InlineData("/opt/homebrew/Cellar/monad/2.0.2/bin/monad")]
    [InlineData("/usr/local/Cellar/monad/2.0.2/bin/monad")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/monad/2.0.2/bin/monad")]
    public void ChannelOf_HomebrewKeepsMonadInItsCellar(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.Homebrew);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\leduftw.monad_Microsoft.Winget.Source_8wekyb3d8bbwe\monad.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\monad.exe")]
    public void ChannelOf_WinGetKeepsMonadInItsPackagesFolder(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.WinGet);
    }

    [Theory]
    [InlineData("/home/me/monad/Monad/bin/Debug/net10.0/monad")]
    [InlineData("/home/me/monad/Monad/bin/Release/net10.0/osx-arm64/monad")]
    [InlineData(@"C:\src\monad\Monad\bin\Debug\net10.0\monad.exe")]
    public void ChannelOf_RecognisesALocalBuild(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.LocalBuild);
    }

    [Theory]
    [InlineData("/usr/local/bin/monad")]
    [InlineData("/home/me/.local/bin/monad")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\monad\bin\monad.exe")]
    public void ChannelOf_AnythingElseCameFromTheInstaller(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.Installer);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.302/libexec/dotnet")]
    public void ChannelOf_RefusesAProgramThatIsNotMonad(string path)
    {
        // `dotnet monad.dll` runs as dotnet; the installer must not land in dotnet's folder.
        Updater.ChannelOf(path).Should().Be(InstallChannel.Unknown);
        Updater.PlanFor(InstallChannel.Unknown, path, windows: false).Should().BeNull();
    }

    [Fact]
    public void Resolve_FollowsLinksBeforeTheChannelIsRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating links there needs developer mode
        }

        // Arrange — Homebrew's bin/monad is a link into its Cellar
        string root = Directory.CreateTempSubdirectory("monad-update-test-").FullName;

        try
        {
            string cellar = Directory.CreateDirectory(Path.Combine(root, "Cellar", "monad", "2.0.2", "bin")).FullName;
            string bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            File.WriteAllText(Path.Combine(cellar, "monad"), string.Empty);
            string link = Path.Combine(bin, "monad");
            File.CreateSymbolicLink(link, Path.Combine(cellar, "monad"));

            // Act / Assert
            Updater.ChannelOf(link).Should().Be(InstallChannel.Installer, "the link alone looks like the installer");
            Updater.ChannelOf(Updater.Resolve(link)).Should().Be(InstallChannel.Homebrew);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- Plans ---

    [Fact]
    public void PlanFor_EachChannelUsesItsOwnTool()
    {
        UpdatePlan homebrew = Updater.PlanFor(InstallChannel.Homebrew, "/opt/homebrew/Cellar/monad/2.0.2/bin/monad", windows: false)!;
        UpdatePlan winget = Updater.PlanFor(InstallChannel.WinGet, @"C:\x\Microsoft\WinGet\Packages\p\monad.exe", windows: true)!;

        $"{homebrew.Program} {string.Join(' ', homebrew.Arguments)}".Should().Be("brew upgrade leduftw/tap/monad");
        homebrew.Detach.Should().BeFalse();
        $"{winget.Program} {string.Join(' ', winget.Arguments)}".Should().Be("winget upgrade --id leduftw.monad");
        winget.Detach.Should().BeTrue("Windows can't replace the running monad.exe");
        Updater.PlanFor(InstallChannel.LocalBuild, "/x/bin/Debug/net10.0/monad", windows: false).Should().BeNull();
    }

    [Fact]
    public void PlanFor_TheInstallerReinstallsIntoTheSameFolder()
    {
        // Arrange
        string executable = Path.Combine(Path.GetTempPath(), "programs", "monad");

        // Act
        UpdatePlan unix = Updater.PlanFor(InstallChannel.Installer, executable, windows: false)!;
        UpdatePlan windows = Updater.PlanFor(InstallChannel.Installer, executable, windows: true)!;

        // Assert
        unix.Program.Should().Be("sh");
        unix.Arguments[1].Should().Contain("monad-installer.sh").And.Contain("curl -fsSL");
        unix.InstallDirectory.Should().Be(Path.GetDirectoryName(executable));
        unix.Detach.Should().BeFalse();
        windows.Program.Should().Be("powershell");
        windows.Arguments[^1].Should().Contain("monad-installer.ps1");
        windows.InstallDirectory.Should().Be(Path.GetDirectoryName(executable));
        windows.Detach.Should().BeTrue();
    }

    // --- Versions ---

    [Fact]
    public void VersionFromLocation_ReadsGitHubsRedirect()
    {
        Updater.VersionFromLocation("https://github.com/leduftw/monad/releases/tag/v2.1.0")
            .Should().Be(new Version(2, 1, 0));
        Updater.VersionFromLocation("https://github.com/leduftw/monad/releases").Should().BeNull();
        Updater.VersionFromLocation(null).Should().BeNull();
    }

    [Theory]
    [InlineData("2.0.2", 2, 0, 2)]
    [InlineData("2.0.2+4a6c031018d622c7eb4c3607a6bdf61e1a07866f", 2, 0, 2)]
    [InlineData("1.10.0", 1, 10, 0)]
    public void ParseVersion_ReadsMajorMinorPatch(string text, int major, int minor, int patch)
    {
        Updater.ParseVersion(text).Should().Be(new Version(major, minor, patch));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("2.0.2.1")]
    [InlineData("latest")]
    [InlineData("")]
    public void ParseVersion_RejectsAnythingElse(string text)
    {
        Updater.ParseVersion(text).Should().BeNull();
    }

    [Fact]
    public void Versions_CompareAsNumbers()
    {
        Updater.ParseVersion("1.10.0").Should().BeGreaterThan(Updater.ParseVersion("1.9.9"));
    }

    // --- Running the plan ---

    [Fact]
    public async Task StartAsync_GivesTheInstallerTheCheckedVersionAndFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — a stand-in for the installer that records what it was given
        string folder = Directory.CreateTempSubdirectory("monad-update-test-").FullName;

        try
        {
            string script = Path.Combine(folder, "installer");
            File.WriteAllText(script, "#!/bin/sh\nprintf '%s %s\\n' \"$MONAD_VERSION\" \"$MONAD_INSTALL_DIR\" > \"$MONAD_INSTALL_DIR/received\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            UpdatePlan plan = new("the installer", script, [], folder, Detach: false);

            // Act
            int exitCode = await Updater.StartAsync(plan, new Version(2, 1, 0), CancellationToken.None);

            // Assert
            exitCode.Should().Be(0);
            File.ReadAllText(Path.Combine(folder, "received")).Should().Be($"2.1.0 {folder}\n");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_ReportsAFailedUpdate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Directory.CreateTempSubdirectory("monad-update-test-").FullName;

        try
        {
            string script = Path.Combine(folder, "manager");
            File.WriteAllText(script, "#!/bin/sh\nexit 9\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            int exitCode = await Updater.StartAsync(
                new UpdatePlan("Homebrew", script, [], null, Detach: false), new Version(2, 1, 0), CancellationToken.None);

            exitCode.Should().Be(9);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
