using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Monad.Update;

using Xunit;

namespace Monad.Tests.Update;

public sealed class UpdaterTests
{
    private const string WinGetFolder = "leduftw.monad_Microsoft.Winget.Source_8wekyb3d8bbwe";

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
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\" + WinGetFolder + @"\monad.exe")]
    [InlineData(@"C:\Program Files\WinGet\Packages\" + WinGetFolder + @"\monad.exe")]
    [InlineData(@"C:\Program Files (x86)\WinGet\Packages\" + WinGetFolder + @"\monad.exe")]
    [InlineData(@"D:\portable\" + WinGetFolder + @"\monad.exe")]
    [InlineData(@"\\?\C:\Program Files\WinGet\Packages\" + WinGetFolder + @"\monad.exe")]
    public void ChannelOf_WinGetKeepsMonadInAFolderOfItsOwn(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.WinGet);
    }

    [Theory]
    [InlineData(@"D:\tools\monad.exe")] // winget install --location puts monad straight into the folder
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\another.package\monad.exe")]
    [InlineData(@"C:\Program Files\WinGet\Packages\" + WinGetFolder + @".old\monad.exe")]
    public void ChannelOf_OnlyThatFolderMeansWinGet(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.Installer);
    }

    [Theory]
    [InlineData("/Users/me/.dotnet/tools/.store/leduftw.monad/2.2.0/leduftw.monad.osx-arm64/2.2.0/tools/any/osx-arm64/monad")]
    [InlineData("/opt/tools/.store/leduftw.monad/2.2.0/leduftw.monad.linux-x64/2.2.0/tools/any/linux-x64/monad")]
    [InlineData(@"C:\Users\me\.dotnet\tools\.store\leduftw.monad\2.2.0\leduftw.monad.win-x64\2.2.0\tools\any\win-x64\monad.exe")]
    public void ChannelOf_NuGetKeepsMonadInTheToolStore(string path)
    {
        Updater.ChannelOf(path).Should().Be(InstallChannel.NuGet);
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
    [InlineData("/Users/me/.nuget/packages/leduftw.monad.osx-arm64/2.2.0/tools/any/osx-arm64/monad")]
    [InlineData(@"C:\Users\me\.nuget\packages\leduftw.monad.win-x64\2.2.0\tools\any\win-x64\monad.exe")]
    public void ChannelOf_RecognisesALocalToolInNuGetsPackageFolder(string path)
    {
        // Not the installer's: running it into NuGet's package folder would corrupt that package.
        Updater.ChannelOf(path).Should().Be(InstallChannel.LocalTool);
        Updater.PlanFor(InstallChannel.LocalTool, path, windows: false).Should().BeNull();
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

    [UnixFact]
    public void Resolve_FollowsLinksBeforeTheChannelIsRead()
    {
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

    [UnixFact]
    public void Resolve_FollowsDirectoryLinksBeforeTheChannelIsRead()
    {
        using Installation installed = new("Cellar", "monad", "2.0.2", "bin");
        string link = Path.Combine(installed.Root, "current");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(installed.Executable)!);

        string resolved = Updater.Resolve(Path.Combine(link, "monad"));

        Updater.ChannelOf(resolved).Should().Be(InstallChannel.Homebrew);
        resolved.Should().NotContain(Path.DirectorySeparatorChar + "current" + Path.DirectorySeparatorChar);
    }

    [UnixFact]
    public void Resolve_FollowsWinGetsLinksIntoItsFolder()
    {
        // Arrange — WinGet's Links folder holds a link to the program in its package folder
        using Installation installed = new("Packages", WinGetFolder);
        string links = Directory.CreateDirectory(Path.Combine(installed.Root, "Links")).FullName;
        string link = Path.Combine(links, "monad");
        File.CreateSymbolicLink(link, installed.Executable);

        // Act / Assert
        Updater.ChannelOf(Updater.Resolve(link)).Should().Be(InstallChannel.WinGet);
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
    public void PlanFor_NuGetUpdatesTheToolInTheFolderHoldingItsStore()
    {
        // Arrange — a global install and one into a folder of the user's choosing
        string global = "/Users/me/.dotnet/tools/.store/leduftw.monad/2.2.0/leduftw.monad.osx-arm64/2.2.0/tools/any/osx-arm64/monad";
        string folder = @"D:\tools\.store\leduftw.monad\2.2.0\leduftw.monad.win-x64\2.2.0\tools\any\win-x64\monad.exe";

        // Act
        UpdatePlan unix = Updater.PlanFor(InstallChannel.NuGet, global, windows: false)!;
        UpdatePlan windows = Updater.PlanFor(InstallChannel.NuGet, folder, windows: true)!;

        // Assert
        $"{unix.Program} {string.Join(' ', unix.Arguments)}".Should()
            .Be("dotnet tool update --tool-path /Users/me/.dotnet/tools leduftw.monad");
        unix.Detach.Should().BeFalse();
        windows.Arguments.Should().Equal("tool", "update", "--tool-path", @"D:\tools", "leduftw.monad");
        windows.Detach.Should().BeTrue("Windows can't replace the running monad.exe");
        Updater.PlanFor(InstallChannel.NuGet, "/usr/local/bin/monad", windows: false).Should().BeNull();
    }

    [Theory]
    [InlineData("/.store/leduftw.monad/2.2.0/tools/any/osx-arm64/monad", "/", false)]
    [InlineData(@"D:\.store\leduftw.monad\2.2.0\tools\any\win-x64\monad.exe", @"D:\", true)]
    [InlineData("D:/.store/leduftw.monad/2.2.0/tools/any/win-x64/monad.exe", "D:/", true)]
    public void PlanFor_NuGetAtFileSystemRoot_PreservesRoot(string executable, string folder, bool windows)
    {
        UpdatePlan plan = Updater.PlanFor(InstallChannel.NuGet, executable, windows)!;

        plan.Arguments.Should().Equal("tool", "update", "--tool-path", folder, "leduftw.monad");
        plan.Detach.Should().Be(windows);
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

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task StartAsync_GivesTheInstallerTheCheckedVersionAndFolder()
    {
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

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task StartAsync_ReportsAFailedUpdate()
    {
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

    [Fact]
    public async Task RunAsync_WhenAlreadyCurrent_SucceedsAfterOneAnonymousRequest()
    {
        using Installation installed = new();
        using UpdateHttpHandler handler = new(_ => RedirectTo("2.0.2"));
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(0);
        output.ToString().Should().Contain("2.0.2 is the latest version");
        error.ToString().Should().BeEmpty();
        handler.Requests.Should().Equal("https://github.com/leduftw/monad/releases/latest");
        handler.HadAuthorization.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WhenWinGetIsNotReady_ReportsTheNewReleaseAndSucceeds()
    {
        using Installation installed = new("WinGet", "Packages", WinGetFolder);
        using UpdateHttpHandler handler = new(request => request.RequestUri!.Host == "github.com"
            ? RedirectTo("2.1.0") : new HttpResponseMessage(HttpStatusCode.NotFound));
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(0);
        output.ToString().Should().Contain("2.1.0 is out, but not on WinGet yet");
        error.ToString().Should().BeEmpty();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_ForALocalTool_RefusesBeforeAnyRequest()
    {
        using Installation installed = new(".nuget", "packages", "leduftw.monad.osx-arm64", "2.0.2", "tools", "any", "osx-arm64");
        using UpdateHttpHandler handler = new(_ => RedirectTo("2.1.0"));
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("is a local .NET tool").And.Contain("dotnet tool update leduftw.monad");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, """{"versions":["2.0.2"]}""")]
    public async Task RunAsync_WhenNuGetIsNotReady_ReportsTheNewReleaseAndSucceeds(HttpStatusCode status, string body)
    {
        using Installation installed = new(NuGetStore);
        using UpdateHttpHandler handler = new(request => request.RequestUri!.Host == "github.com"
            ? RedirectTo("2.1.0") : new HttpResponseMessage(status) { Content = new StringContent(body) });
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(0);
        output.ToString().Should().Contain("2.1.0 is out, but not on NuGet yet");
        error.ToString().Should().BeEmpty();
        handler.Requests.Should().Equal(
            "https://github.com/leduftw/monad/releases/latest",
            "https://api.nuget.org/v3-flatcontainer/leduftw.monad/index.json");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "", "it answered 500")]
    [InlineData(HttpStatusCode.OK, "{}", "lists no versions")]
    [InlineData(HttpStatusCode.OK, "[]", "lists no versions")]
    [InlineData(HttpStatusCode.OK, "null", "lists no versions")]
    [InlineData(HttpStatusCode.OK, """{"versions":[42]}""", "invalid version")]
    [InlineData(HttpStatusCode.OK, """{"versions":[null]}""", "invalid version")]
    [InlineData(HttpStatusCode.OK, """{"versions":["2.1.0",null]}""", "invalid version")]
    [InlineData(HttpStatusCode.OK, "<html>", "is an invalid start of a value")]
    public async Task RunAsync_WhenNuGetCannotBeRead_ReportsExitOne(HttpStatusCode status, string body, string reason)
    {
        using Installation installed = new(NuGetStore);
        using UpdateHttpHandler handler = new(request => request.RequestUri!.Host == "github.com"
            ? RedirectTo("2.1.0") : new HttpResponseMessage(status) { Content = new StringContent(body) });
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().StartWith("monad: can't check NuGet for 2.1.0").And.Contain(reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenAReleaseOrWinGetRequestTimesOut_ReportsExitOne(bool latestRequest)
    {
        using Installation installed = new("WinGet", "Packages", WinGetFolder);
        using UpdateHttpHandler handler = new(request => latestRequest || request.RequestUri!.Host != "github.com"
            ? throw new TaskCanceledException("request timed out") : RedirectTo("2.1.0"));
        using HttpClient http = new(handler);
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Updater.RunAsync("2.0.2", installed.Executable, http, output, error, CancellationToken.None);

        code.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().StartWith("monad: can't check").And.Contain("request timed out");
        handler.Requests.Should().HaveCount(latestRequest ? 1 : 2);
    }

    /// <summary>Where <c>dotnet tool install</c> puts monad on macOS, below the folder it installs into.</summary>
    private static readonly string[] NuGetStore =
        [".store", "leduftw.monad", "2.0.2", "leduftw.monad.osx-arm64", "2.0.2", "tools", "any", "osx-arm64"];

    private static HttpResponseMessage RedirectTo(string version) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri($"https://github.com/leduftw/monad/releases/tag/v{version}") },
    };

    private sealed class UpdateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool HadAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!.AbsoluteUri);
            this.HadAuthorization |= request.Headers.Authorization is not null;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Installation : IDisposable
    {
        public Installation(params string[] folders)
        {
            this.Root = Directory.CreateTempSubdirectory("monad-update-test-").FullName;
            this.Executable = Path.Combine([this.Root, .. folders, OperatingSystem.IsWindows() ? "monad.exe" : "monad"]);
            Directory.CreateDirectory(Path.GetDirectoryName(this.Executable)!);
            File.WriteAllText(this.Executable, string.Empty);
        }

        public string Root { get; }

        public string Executable { get; }

        public void Dispose() => Directory.Delete(this.Root, recursive: true);
    }
}
