using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Monad.Aggregation;

namespace Monad.Output;

/// <summary>Somewhere finished segments are reported to.</summary>
public interface ISegmentSink : IAsyncDisposable
{
    ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken);
}

/// <summary>Prints segments in local time, one line each.</summary>
public sealed class ConsoleSegmentSink(TextWriter? writer = null) : ISegmentSink
{
    private readonly TextWriter output = writer ?? Console.Out;

    public ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        this.output.WriteLine(Format(segment));

        return ValueTask.CompletedTask;
    }

    public static string Format(Segment segment)
    {
        string start = segment.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string end = segment.EndUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        return $"SEGMENT  [{start} - {end}]  {FormatDuration(segment.Duration)}  {segment.Label}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int totalSeconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds));

        return $"{totalSeconds / 60,3}m{totalSeconds % 60:00}s";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Appends one JSON object per line, so a session leaves a durable record that
/// survives the terminal scrollback and is trivial to query afterwards.
/// </summary>
public sealed class JsonlSegmentSink : ISegmentSink
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private readonly StreamWriter writer;

    public JsonlSegmentSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        this.FilePath = full;
        this.writer = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true, // a monitoring run can be killed at any moment
        };
    }

    public string FilePath { get; }

    public async ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await this.writer.WriteLineAsync(Serialize(segment).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public static string Serialize(Segment segment)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("kind", segment.Kind.ToString().ToLowerInvariant());
            json.WriteString("startUtc", segment.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("endUtc", segment.EndUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteNumber("durationSeconds", Math.Round(segment.Duration.TotalSeconds, 3));

            if (segment.SongKey is not null)
            {
                json.WriteString("songKey", segment.SongKey);
            }

            if (segment.DisplayText is not null)
            {
                json.WriteString("display", segment.DisplayText);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async ValueTask DisposeAsync() => await this.writer.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Fans segments out to several sinks.</summary>
public sealed class CompositeSegmentSink(IReadOnlyList<ISegmentSink> sinks) : ISegmentSink
{
    public async ValueTask EmitAsync(Segment segment, CancellationToken cancellationToken)
    {
        foreach (ISegmentSink sink in sinks)
        {
            await sink.EmitAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ISegmentSink sink in sinks)
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }
}
