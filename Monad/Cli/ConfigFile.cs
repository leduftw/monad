using System;
using System.IO;
using System.Text.Json;

namespace Monad.Cli;

/// <summary>
/// The on-disk config. Everything is optional; anything omitted keeps its
/// default, and anything named on the command line wins over both.
/// </summary>
public sealed record class ConfigFile
{
    public string? Token { get; init; }

    public string? Jsonl { get; init; }

    public string? Source { get; init; }

    /// <summary>Every knob in <see cref="MonadConfig"/>, by its own name.</summary>
    public MonadConfig? Tuning { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ConfigFile Empty { get; } = new();

    /// <summary>
    /// Looks for the config in the usual places. An explicit path must exist;
    /// the conventional locations are tried silently.
    /// </summary>
    public static (ConfigFile Config, string? Path) Discover(string? explicitPath) => Discover(explicitPath, Load);

    /// <summary>Uses the same discovery policy with an explicit config reader.</summary>
    public static (ConfigFile Config, string? Path) Discover(string? explicitPath, Func<string, ConfigFile> readConfig)
    {
        ArgumentNullException.ThrowIfNull(readConfig);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            try
            {
                return (readConfig(explicitPath), explicitPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Name the path as it was given, not as an absolute path from the runtime.
                throw new FileNotFoundException($"Config file '{explicitPath}' does not exist.", explicitPath, ex);
            }
        }

        foreach (string candidate in DefaultPaths())
        {
            try
            {
                return (readConfig(candidate), candidate);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // File.Exists also returns false when access is denied. Only
                // a missing path is optional; an unreadable config is an error.
            }
        }

        return (Empty, null);
    }

    public static ConfigFile Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), Options) ?? Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config file '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private static string[] DefaultPaths()
    {
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return
        [
            Path.Combine(Directory.GetCurrentDirectory(), "monad.json"),
            Path.Combine(configHome, "monad", "config.json"),
        ];
    }
}
