using System.Collections.Generic;
using System.CommandLine;
using System.Linq;

namespace Monad.Cli;

/// <summary>
/// monad's help, drawn from System.CommandLine's own definitions so it lists
/// exactly what the parser accepts, in the palette of Python 3.14's argparse.
/// </summary>
public static class HelpText
{
    public static StyledText For(Command command, RootCommand root)
    {
        bool isRoot = ReferenceEquals(command, root);
        string program = isRoot ? "monad" : $"monad {command.Name}";
        StyledText help = new();

        help.Add(isRoot ? CommandLineParser.Description : command.Description ?? string.Empty).Add("\n\n");

        help.Add("Usage:", StyledText.Heading).Add(" ").Add(program, StyledText.ProgramName)
            .Add(" ").Add("[options]", StyledText.UsageValue);

        foreach (Argument argument in Visible(command.Arguments))
        {
            help.Add(" ").Add(Placeholder(argument), StyledText.UsageValue);
        }

        if (isRoot)
        {
            help.Add("\n       ").Add(program, StyledText.ProgramName)
                .Add(" ").Add("<command>", StyledText.UsageValue)
                .Add(" ").Add("[options]", StyledText.UsageValue);
        }

        List<(StyledText Name, string Description)> commands = Visible(command.Subcommands)
            .Select(sub => (new StyledText().Add(sub.Name, StyledText.CommandName), sub.Description ?? string.Empty))
            .ToList();
        Section(help, "Commands", commands);

        List<(StyledText Name, string Description)> arguments = Visible(command.Arguments)
            .Select(argument => (
                new StyledText().Add(Placeholder(argument), StyledText.CommandName),
                argument.Description ?? string.Empty))
            .ToList();
        Section(help, "Arguments", arguments);

        // A command's own options, then the ones it inherits, such as --help.
        IEnumerable<Option> options = command.Options;

        if (!isRoot)
        {
            options = options.Concat(root.Options.Where(option => option.Recursive && !command.Options.Contains(option)));
        }

        Section(help, "Options", Visible(options).Select(option => (Spec(option), option.Description ?? string.Empty)).ToList());

        return help.Add("\n");
    }

    private static StyledText Spec(Option option)
    {
        string[] names = [option.Name, .. option.Aliases];
        string[] shorts = names.Where(name => !name.StartsWith("--", System.StringComparison.Ordinal)).ToArray();
        string[] longs = names.Where(name => name.StartsWith("--", System.StringComparison.Ordinal)).ToArray();
        StyledText spec = new();

        if (shorts.Length == 0)
        {
            spec.Add("    ");
        }

        foreach (string name in shorts)
        {
            spec.Add(name, StyledText.ShortOption).Add(", ");
        }

        for (int i = 0; i < longs.Length; i++)
        {
            spec.Add(i == 0 ? string.Empty : ", ").Add(longs[i], StyledText.LongOption);
        }

        if (option.ValueType != typeof(bool))
        {
            spec.Add(" ").Add($"<{option.HelpName ?? option.Name.TrimStart('-')}>", StyledText.Value);
        }

        return spec;
    }

    private static string Placeholder(Argument argument) =>
        argument.Arity.MinimumNumberOfValues == 0 ? $"[<{argument.Name}>]" : $"<{argument.Name}>";

    private static void Section(StyledText help, string heading, List<(StyledText Name, string Description)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        int width = rows.Max(row => row.Name.Width);
        help.Add("\n\n").Add($"{heading}:", StyledText.Heading);

        foreach ((StyledText name, string description) in rows)
        {
            help.Add("\n  ").Add(name).Add(new string(' ', width - name.Width + 2)).Add(description);
        }
    }

    private static IEnumerable<T> Visible<T>(IEnumerable<T> symbols)
        where T : Symbol => symbols.Where(symbol => !symbol.Hidden);
}
