using System.Globalization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Server;

internal sealed class OfficeDiagnostics
{
    private const int Capacity = 64;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly (string? Version, string? BuildId) _build = ReadBuild();
    private readonly Queue<Entry> _events = new(Capacity);
    private readonly object _gate = new();

    internal string? Version => _build.Version;

    public void Record(string code, string operation, Exception exception)
    {
        lock (_gate)
        {
            if (_events.Count == Capacity) _events.Dequeue();
            _events.Enqueue(new(DateTimeOffset.UtcNow, code, operation, exception.GetType().Name, ErrorCode(exception)));
        }
    }

    public IResult Read()
    {
        Entry[] entries;
        lock (_gate) entries = _events.ToArray();
        return Results.Ok(new { startedAt = _startedAt, version = Version, buildId = _build.BuildId, events = entries });
    }

    private static string ErrorCode(Exception exception)
    {
        const string label = "Код: ";
        var message = exception.Message.AsSpan();
        var suffixLength = label.Length + 8;
        if (exception is WorkflowValidationException && message.Length >= suffixLength && message[^suffixLength..^8].SequenceEqual(label) &&
            uint.TryParse(message[^8..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            return code.ToString("X8", CultureInfo.InvariantCulture);
        return exception.HResult.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static (string? Version, string? BuildId) ReadBuild()
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "install-build.txt"));
            if (lines is [var version, var hash] && version is { Length: >= 2 and <= 9 } && version[0] == 'v' &&
                version[1..].All(char.IsAsciiDigit) && hash is { Length: 64 } && hash.All(Uri.IsHexDigit))
                return (version, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return (null, null);
    }

    private sealed record Entry(DateTimeOffset At, string Code, string Operation, string Exception, string ErrorCode);
}
