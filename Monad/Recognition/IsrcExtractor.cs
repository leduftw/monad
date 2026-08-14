using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Monad.Recognition;

/// <summary>
/// Pulls ISRC codes out of a recognition result. The same recording can be
/// reported by the service itself, by Spotify and by Apple Music, and they do
/// not always agree -- so all three are collected and any disagreement is
/// reported rather than hidden behind whichever one happened to be checked
/// first.
/// </summary>
public static class IsrcExtractor
{
    public static IsrcInfo Extract(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return IsrcInfo.None;
        }

        string? topLevel = Normalize(ReadString(result, "isrc"));
        string? spotify = Normalize(ReadNestedString(result, "spotify", "external_ids", "isrc"));
        string? apple = Normalize(ReadNestedString(result, "apple_music", "isrc"));

        // Preference order: the service's own code, then Spotify, then Apple.
        string? selected = topLevel ?? spotify ?? apple;

        if (selected is null)
        {
            return IsrcInfo.None;
        }

        string[] distinct = new[] { topLevel, spotify, apple }
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinct.Length <= 1)
        {
            return new IsrcInfo(selected, topLevel, spotify, apple, $"[ISRC: {selected}]");
        }

        List<string> parts = [];
        Describe(parts, topLevel, "result");
        Describe(parts, spotify, "spotify");
        Describe(parts, apple, "apple");

        return new IsrcInfo(selected, topLevel, spotify, apple, $"[ISRC mismatch: {string.Join(", ", parts)}]")
        {
            HasMismatch = true,
        };
    }

    private static void Describe(List<string> parts, string? code, string label)
    {
        if (code is not null)
        {
            parts.Add($"{label}={code}");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>
    /// Reads a string property, tolerating a field that is present but holds
    /// some other JSON type. <c>GetString()</c> throws in that case, which used
    /// to take the whole recognition loop down.
    /// </summary>
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;

        for (int i = 0; i < path.Length - 1; i++)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Object ? ReadString(current, path[^1]) : null;
    }
}
