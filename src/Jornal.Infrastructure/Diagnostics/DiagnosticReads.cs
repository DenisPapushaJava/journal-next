using System.Text.Json.Serialization;

namespace Jornal.Infrastructure.Diagnostics;

public enum DiagnosticReadAction { Unknown, ReadOrders, ReadDay, ReadOrder, ReadMachineCatalog, ReadPriceTable }
public enum DiagnosticReadOutcome { Unknown, Started, Succeeded, Failed, Cancelled }
// Описывает синтаксис пути, а не доказанную доступность сети или тип подключённого диска.
public enum DiagnosticPathKind { Unknown, UncPath, DrivePath, AbsolutePath, RelativePath }

public sealed record DiagnosticReadContext(DiagnosticReadAction Action, DiagnosticPathKind PathKind, long DurationMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSuccessfulReadUtc = null)
{
    public void Validate()
    {
        if (!DiagnosticReads.ValidAction(Action) || !Enum.IsDefined(PathKind) || !DiagnosticReads.ValidDuration(DurationMilliseconds))
            throw new InvalidDataException("Недопустимый контекст чтения.");
    }
}

// Контекст принадлежит одному async-вызову. Он не запускает I/O, таймеры или проверку существования файла.
internal static class DiagnosticReads
{
    private static readonly object ContextKey = new();
    private static readonly AsyncLocal<Observation?> Current = new();
    internal const long MaximumDurationMilliseconds = 86_400_000;
    internal static bool ValidAction(DiagnosticReadAction action) => action != DiagnosticReadAction.Unknown && Enum.IsDefined(action);
    internal static bool ValidDuration(long value) => value is >= 0 and <= MaximumDurationMilliseconds;
    internal static bool ValidBreadcrumb(DiagnosticBreadcrumb value) =>
        value.Action == DiagnosticReadAction.Unknown && value.Outcome == DiagnosticReadOutcome.Unknown && value.DurationMilliseconds is null ||
        ValidAction(value.Action) && Enum.IsDefined(value.Outcome) && value.Outcome != DiagnosticReadOutcome.Unknown &&
        (value.Outcome == DiagnosticReadOutcome.Started ? value.DurationMilliseconds is null : value.DurationMilliseconds is { } duration && ValidDuration(duration));

    internal static DiagnosticPathKind ClassifyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return DiagnosticPathKind.Unknown;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return DiagnosticPathKind.UncPath;
        var drive = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        if (drive.Length >= 3 && char.IsAsciiLetter(drive[0]) && drive[1] == ':' && drive[2] is '\\' or '/') return DiagnosticPathKind.DrivePath;
        return path[0] is '/' or '\\' ? DiagnosticPathKind.AbsolutePath : DiagnosticPathKind.RelativePath;
    }

    internal static async Task<T> ObserveAsync<T>(DiagnosticOperation operation, DiagnosticReadAction action, string path, Func<Task<T>> read)
    {
        var previous = Current.Value;
        Observation? observation = null;
        try { observation = new(AppDiagnostics.Current, operation, action, path); }
        catch (Exception) { /* Диагностика не меняет результат рабочего чтения. */ }
        Current.Value = observation;
        try
        {
            var result = await read();
            observation?.Finish(DiagnosticReadOutcome.Succeeded, null);
            return result;
        }
        catch (Exception exception)
        {
            observation?.Finish(exception is OperationCanceledException ? DiagnosticReadOutcome.Cancelled : DiagnosticReadOutcome.Failed, exception);
            throw;
        }
        finally { Current.Value = previous; }
    }

    internal static DiagnosticReadContext? ContextOf(Exception exception)
    {
        try
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
                if (current.Data[ContextKey] is DiagnosticReadContext context) return context;
        }
        catch (Exception) { }
        return null;
    }

    // Хост уже сформировал событие: дополняем его до постановки в очередь, без повторного Capture/EventId.
    internal static DiagnosticReport EnrichHostReport(DiagnosticReport report)
    {
        if (report.WriteFailure is not null || report.Operation is not (DiagnosticOperation.OrderRead or DiagnosticOperation.PriceRead)) return report;
        try { return Current.Value is { } observation ? observation.Enrich(report) : report; }
        catch (Exception) { return report; }
    }

    private sealed class Observation
    {
        private readonly DiagnosticService? _service;
        private readonly TimeProvider _clock;
        private readonly long _started;
        private readonly string _source;
        private readonly DiagnosticReadAction _action;
        private readonly DiagnosticPathKind _kind;
        private readonly LinkedListNode<DiagnosticBreadcrumb>? _entry;
        internal Observation(DiagnosticService? service, DiagnosticOperation operation, DiagnosticReadAction action, string source)
        {
            _service = service; _clock = service?.ReadClock ?? TimeProvider.System;
            _started = _clock.GetTimestamp(); _source = source; _action = action; _kind = ClassifyPath(source);
            _entry = service?.StartRead(new(operation, _clock.GetUtcNow(), action, DiagnosticReadOutcome.Started));
        }
        internal DiagnosticReadContext Snapshot() => new(_action, _kind,
            (long)Math.Clamp(_clock.GetElapsedTime(_started).TotalMilliseconds, 0, MaximumDurationMilliseconds), _service?.LastSuccessfulRead(_source));
        internal DiagnosticReport Enrich(DiagnosticReport report)
        {
            var context = Snapshot();
            return report with { ReadContext = context, Recent = report.Recent.Length == 0 && _service is not null
                ? _service.ReadHistoryAtFailure(_entry, context.DurationMilliseconds) : report.Recent };
        }
        internal void Finish(DiagnosticReadOutcome outcome, Exception? exception)
        {
            try
            {
                var context = Snapshot();
                _service?.FinishRead(_entry, _source, outcome, context.DurationMilliseconds, _clock.GetUtcNow());
                if (exception is not null && ContextOf(exception) is null) exception.Data[ContextKey] = context;
            }
            catch (Exception) { }
        }
    }
}
