using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace Jornal.Infrastructure.Diagnostics;

public sealed record DiagnosticReceipt(int Schema, string EventId, bool Accepted);

public sealed class DiagnosticService : IDisposable
{
    private readonly DiagnosticStore _store;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly string _version;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private sealed record QueuedRecord(DiagnosticReport Report, long Generation, bool Automatic);
    private sealed record AutomaticChoice(bool Enabled, long Generation);
    private volatile AutomaticChoice _choice = new(true, 0);
    private readonly Channel<QueuedRecord> _records = Channel.CreateBounded<QueuedRecord>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly LinkedList<DiagnosticBreadcrumb> _recent = new();
    private readonly Dictionary<string, DateTimeOffset> _successfulReads = new(StringComparer.Ordinal);
    internal TimeProvider ReadClock { get; }
    private readonly object _recentGate = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _sending = new();
    private bool _enabled;
    private readonly Task _writer;
    private Task? _sender;
    private volatile bool _disposed;
    private long _generation;
    private int _persisted;
    private int _submitted;
    public bool StorageAvailable { get; private set; } = true;
    public bool PreferencesSaved { get; private set; } = true;
    public bool Configured => _endpoint is not null;
    public bool SendAutomatically { get { lock (_gate) return _enabled; } }
    public int PendingCount => _store.PendingCount;
    public bool HasPreviousFatal { get; private set; }
    public DateTimeOffset? LastAcceptedUtc { get; private set; }
    public DiagnosticStore Store => _store;
    public string SessionId => _session;

    public DiagnosticService(DiagnosticStore store, HttpClient http, Uri? endpoint, string version, TimeProvider? timeProvider = null)
    {
        if (endpoint is not null && (endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)))
            throw new ArgumentException("Нужен HTTPS-адрес приёмника без учётных данных.", nameof(endpoint));
        _store = store; _http = http; _endpoint = endpoint; _version = version; ReadClock = timeProvider ?? TimeProvider.System;
        _enabled = store.LoadAutomatic(); PreferencesSaved = !store.SettingsUnreadable;
        _choice = new(_enabled, 0);
        if (!_enabled) store.ClearQueue();
        HasPreviousFatal = store.HasFatal;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Capture(DiagnosticOperation operation, Exception exception, bool fatal = false)
    {
        var report = DiagnosticReport.Create(exception, operation, _session, _version,
            fatal ? DiagnosticSeverity.Fatal : DiagnosticSeverity.Error, recent: Recent());
        if (fatal)
        {
            lock (_gate) StorageAvailable = _store.Save(report, _enabled);
        }
        else Queue(report);
        return report.Code;
    }
    private void Queue(DiagnosticReport report)
    {
        // Захват из UI не ждёт файловую запись отправителя/смены настройки.
        var choice = _choice;
        if (!_disposed && _records.Writer.TryWrite(new(report, choice.Generation, choice.Enabled))) Interlocked.Increment(ref _submitted);
    }
    public void Record(DiagnosticReport report) { try { report.Validate(); Queue(report); } catch (Exception) { } }
    public void Mark(DiagnosticOperation operation)
    {
        lock (_recentGate)
        {
            if (_recent.Count == 16) _recent.RemoveFirst();
            _recent.AddLast(new DiagnosticBreadcrumb(operation, ReadClock.GetUtcNow()));
        }
    }
    internal LinkedListNode<DiagnosticBreadcrumb>? StartRead(DiagnosticBreadcrumb entry)
    {
        lock (_recentGate)
        {
            if (_disposed) return null;
            if (_recent.Count == 16) _recent.RemoveFirst();
            return _recent.AddLast(entry);
        }
    }
    internal DateTimeOffset? LastSuccessfulRead(string source)
    { lock (_recentGate) return _successfulReads.TryGetValue(source, out var at) ? at : null; }
    internal DiagnosticBreadcrumb[] ReadHistoryAtFailure(LinkedListNode<DiagnosticBreadcrumb>? entry, long duration)
    {
        lock (_recentGate)
            return _recent.Select(item => entry?.List == _recent && ReferenceEquals(item, entry.Value)
                ? item with { Outcome = DiagnosticReadOutcome.Failed, DurationMilliseconds = duration } : item).ToArray();
    }
    internal void FinishRead(LinkedListNode<DiagnosticBreadcrumb>? entry, string source, DiagnosticReadOutcome outcome, long duration, DateTimeOffset at)
    {
        lock (_recentGate)
        {
            if (_disposed) return;
            if (entry?.List == _recent) entry.Value = entry.Value with { Outcome = outcome, DurationMilliseconds = duration };
            // Ключи остаются только в памяти этого сеанса, не входят в отчёт и ограничены 16 источниками.
            if (outcome != DiagnosticReadOutcome.Succeeded || source.Length > 4096) return;
            if (!_successfulReads.ContainsKey(source) && _successfulReads.Count == 16)
                _successfulReads.Remove(_successfulReads.MinBy(pair => pair.Value).Key);
            _successfulReads[source] = at;
        }
    }
    private DiagnosticBreadcrumb[] Recent() { lock (_recentGate) return _recent.ToArray(); }
    public void AcknowledgePreviousFatal() { _store.AcknowledgeFatal(); HasPreviousFatal = false; }
    public void SetAutomatic(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled; _generation++;
            _choice = new(enabled, _generation);
            _sending.Cancel(); _sending.Dispose(); _sending = new();
            PreferencesSaved = _store.SaveAutomatic(enabled);
            if (!enabled) _store.ClearQueue();
        }
    }
    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var report in _records.Reader.ReadAllAsync())
            {
                lock (_gate)
                {
                    StorageAvailable = _store.Save(report.Report, _enabled && report.Automatic && report.Generation == _generation);
                    Interlocked.Increment(ref _persisted);
                }
            }
        }
        catch (Exception) { StorageAvailable = false; }
    }
    public void Start()
    {
        lock (_gate) if (!_disposed && _sender is null) _sender = Task.Run(SendLoopAsync);
    }
    private async Task SendLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do { await SendDueAsync(_lifetime.Token); } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Диагностика не выдаёт исключений в UI, повтор — после следующего запуска. */ }
    }
    public async Task SendDueAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint is null) return;
        var acquired = false;
        try
        {
            acquired = await _sendGate.WaitAsync(0, cancellationToken);
            if (!acquired) return;
            foreach (var item in _store.Due(DateTimeOffset.UtcNow))
            {
                long generation;
                CancellationToken stop;
                lock (_gate)
                {
                    if (!_enabled || _disposed) return;
                    generation = _generation; stop = _sending.Token;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop, _lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var response = await SendReportAsync(item.Report, timeout.Token);
                    lock (_gate)
                    {
                        if (!_enabled || _generation != generation) return;
                        if (response.Accepted)
                        {
                            _store.Complete(item.Report.EventId); LastAcceptedUtc = DateTimeOffset.UtcNow;
                        }
                        else if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
                            _store.Complete(item.Report.EventId); // Отчёт остаётся в локальном журнале для ручного экспорта.
                        else
                        {
                            var now = DateTimeOffset.UtcNow;
                            _store.Retry(item, now, response.RetryAfter);
                            return; // При недоступности не перебираем всю очередь.
                        }
                    }
                }
                catch (Exception)
                {
                    lock (_gate) if (_enabled && _generation == generation && !_disposed) _store.Retry(item, DateTimeOffset.UtcNow);
                    return;
                }
            }
        }
        catch (Exception) { }
        finally { if (acquired) _sendGate.Release(); }
    }
    private sealed record SendResult(HttpStatusCode StatusCode, bool Accepted, TimeSpan? RetryAfter);
    private async Task<SendResult> SendReportAsync(DiagnosticReport original, CancellationToken token)
    {
        var report = original;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            { Content = JsonContent.Create(report, options: DiagnosticReport.Json) };
            request.Headers.UserAgent.ParseAdd("Journal-Diagnostics/1");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            // Старый приёмник отклоняет новые поля до записи Issue. Один повтор прежнего
            // формата с тем же EventId, в пределах общего 8-секундного срока; локальный отчёт полон.
            if (attempt == 0 && report.HasReadDetails && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            { report = original.WithoutReadDetails(); continue; }
            var accepted = false;
            if (response.IsSuccessStatusCode)
            {
                var bytes = await ReadLimitedAsync(response.Content, 4096, token);
                var receipt = JsonSerializer.Deserialize<DiagnosticReceipt>(bytes, DiagnosticReport.Json);
                accepted = receipt is { Schema: 1, Accepted: true } && receipt.EventId == original.EventId;
            }
            return new(response.StatusCode, accepted, response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
        }
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public async Task FlushLocalAsync(CancellationToken token = default)
    {
        // Снимок очереди нужен только для экспорта/тестов. В штатном закрытии сеть не ожидается.
        var target = Volatile.Read(ref _submitted);
        while (Volatile.Read(ref _persisted) < target && !_writer.IsCompleted) await Task.Delay(10, token);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _sending.Cancel(); _records.Writer.TryComplete();
        }
        // Ограниченное ожидание локальной записи; не ждём HTTP и не бросаем исключения из завершения.
        try { _writer.Wait(TimeSpan.FromMilliseconds(500)); } catch (Exception) { }
    }
}
