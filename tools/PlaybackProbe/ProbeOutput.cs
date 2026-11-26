using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsXQuiet.Tools.PlaybackProbe;

internal sealed class ProbeOutput : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    static ProbeOutput()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private readonly StreamWriter? _logWriter;
    private readonly object _gate = new();
    private int _clockWidth;

    private ProbeOutput(StreamWriter? logWriter)
    {
        _logWriter = logWriter;
    }

    public static Task<ProbeOutput> CreateAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(new ProbeOutput(null));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        return Task.FromResult(new ProbeOutput(writer));
    }

    public void Write(ProbeRecord record, bool jsonOnly)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        lock (_gate)
        {
            _logWriter?.WriteLine(json);
            if (jsonOnly)
            {
                ClearClockLine();
                Console.WriteLine(json);
                return;
            }

            if (record.Kind == "clock" && record.Snapshot is not null)
            {
                WriteClockLine(record.Snapshot);
                return;
            }

            ClearClockLine();
            WriteHuman(record);
        }
    }

    public void WriteClock(PlaybackSnapshot snapshot, bool jsonOnly) =>
        Write(ProbeRecord.Clock(snapshot), jsonOnly);

    private void WriteHuman(ProbeRecord record)
    {
        var time = record.Timestamp.ToString("HH:mm:ss");
        switch (record.Kind)
        {
            case "snapshot" when record.Snapshot is not null:
                var snapshot = record.Snapshot;
                var current = snapshot.IsCurrent ? "CURRENT" : "SESSION";
                Console.WriteLine($"[{time}] [{current}] {snapshot.SourceAppUserModelId} · {record.Reason}");
                Console.WriteLine($"  {ValueOrUnknown(snapshot.Title)} — {ValueOrUnknown(snapshot.Artist)}");
                Console.WriteLine($"  {snapshot.PlaybackStatus}  {Format(snapshot.Position)} / {Format(snapshot.EndTime)}  rate {snapshot.PlaybackRate:0.##}x");
                Console.WriteLine($"  controls play={snapshot.CanPlay} pause={snapshot.CanPause} seek={snapshot.CanSeek} next={snapshot.CanNext} previous={snapshot.CanPrevious}");
                break;
            case "error":
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{time}] ERROR {record.Message} {record.ErrorType}: {record.ErrorDetail}");
                Console.ResetColor();
                break;
            default:
                Console.WriteLine($"[{time}] {record.Message}");
                break;
        }
    }

    private void WriteClockLine(PlaybackSnapshot snapshot)
    {
        var estimated = snapshot.EstimatePosition(DateTimeOffset.Now);
        var text = $"\r{snapshot.PlaybackStatus,-7} {Format(estimated)} / {Format(snapshot.EndTime)}  {ValueOrUnknown(snapshot.Title)} — {ValueOrUnknown(snapshot.Artist)}";
        _clockWidth = Math.Max(_clockWidth, text.Length);
        Console.Write(text.PadRight(_clockWidth));
    }

    private void ClearClockLine()
    {
        if (_clockWidth == 0)
        {
            return;
        }

        Console.Write($"\r{new string(' ', _clockWidth)}\r");
        _clockWidth = 0;
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            ClearClockLine();
        }

        if (_logWriter is not null)
        {
            await _logWriter.DisposeAsync();
        }
    }
}
