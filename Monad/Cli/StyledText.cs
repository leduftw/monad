using System.Collections.Generic;
using System.Text;

namespace Monad.Cli;

/// <summary>
/// Text for the terminal, kept in pieces so it prints with the playbook's
/// colors on a terminal and without them anywhere else.
/// </summary>
public sealed class StyledText
{
    // The palette of Python 3.14's argparse, which every playbook tool's help uses.
    public const string Heading = "\u001b[1;34m";
    public const string ProgramName = "\u001b[1;35m";
    public const string CommandName = "\u001b[1;32m";
    public const string ShortOption = "\u001b[1;32m";
    public const string LongOption = "\u001b[1;36m";
    public const string Value = "\u001b[1;33m";

    /// <summary>Values inside the usage line lose the bold.</summary>
    public const string UsageValue = "\u001b[33m";

    private const string Reset = "\u001b[0m";

    private readonly List<(string Text, string? Style)> parts = [];

    public StyledText Add(string text, string? style = null)
    {
        this.parts.Add((text, style));
        return this;
    }

    public StyledText Add(StyledText other)
    {
        this.parts.AddRange(other.parts);
        return this;
    }

    /// <summary>How many characters the text takes on screen.</summary>
    public int Width
    {
        get
        {
            int width = 0;

            foreach ((string text, _) in this.parts)
            {
                width += text.Length;
            }

            return width;
        }
    }

    public string Render(bool color)
    {
        StringBuilder builder = new();

        foreach ((string text, string? style) in this.parts)
        {
            if (color && style is not null)
            {
                builder.Append(style).Append(text).Append(Reset);
            }
            else
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    public override string ToString() => this.Render(color: false);
}
