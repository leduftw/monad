using System;
using System.Collections.Generic;

using FluentAssertions;

using Monad.Cli;

using Xunit;

namespace Monad.Tests.Cli;

public sealed class TerminalColorsTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string?> values = [];

        foreach ((string name, string value) in variables)
        {
            values[name] = value;
        }

        return name => values.GetValueOrDefault(name);
    }

    [Fact]
    public void Allowed_NeverColorsRedirectedOutput()
    {
        TerminalColors.Allowed(redirected: true, noColor: false, Environment(), toError: false).Should().BeFalse();
    }

    [Fact]
    public void Allowed_HonoursTheNoColorFlag()
    {
        TerminalColors.Allowed(redirected: false, noColor: true, Environment(), toError: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("NO_COLOR", "1")]
    [InlineData("NO_COLOR", "anything")]
    [InlineData("TERM", "dumb")]
    public void Allowed_HonoursTheEnvironment(string name, string value)
    {
        TerminalColors.Allowed(redirected: false, noColor: false, Environment((name, value)), toError: true)
            .Should().BeFalse();
    }

    [UnixFact]
    public void Allowed_ColorsATerminal()
    {
        TerminalColors.Allowed(redirected: false, noColor: false, Environment(("NO_COLOR", "")), toError: false)
            .Should().BeTrue("an empty NO_COLOR doesn't count");
    }
}
