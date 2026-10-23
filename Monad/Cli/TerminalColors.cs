using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Monad.Cli;

/// <summary>Whether text for one of the standard streams may carry colors.</summary>
public static class TerminalColors
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// Colors only reach a terminal, and <c>--no-color</c>, <c>NO_COLOR</c>
    /// (set and not empty) or <c>TERM=dumb</c> turn them off.
    /// </summary>
    public static bool Allowed(bool redirected, bool noColor, Func<string, string?> readEnvironment, bool toError)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        if (redirected
            || noColor
            || readEnvironment("NO_COLOR") is { Length: > 0 }
            || readEnvironment("TERM") == "dumb")
        {
            return false;
        }

        // A Windows console prints escape codes as text until it is told to read them.
        return !OperatingSystem.IsWindows() || EnableVirtualTerminal(toError ? StdErrorHandle : StdOutputHandle);
    }

    [SupportedOSPlatform("windows")]
    private static bool EnableVirtualTerminal(int stream)
    {
        IntPtr handle = GetStdHandle(stream);

        return GetConsoleMode(handle, out uint mode)
            && ((mode & EnableVirtualTerminalProcessing) != 0
                || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
