using Jornal.Infrastructure.Diagnostics;
using System.Text.Json.Serialization;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Trash;

public sealed record TrashSource(string Kind, string Path)
{
    public void Validate()
    {
        if (Kind is not ("mdb" or "local" or "demo") || string.IsNullOrWhiteSpace(Path) || Kind != "demo" && !System.IO.Path.IsPathFullyQualified(Path))
            throw new InvalidDataException("Неизвестный источник корзины.");
    }
    [JsonIgnore] public string Key => Kind + ":" + (OperatingSystem.IsWindows() ? Path.ToUpperInvariant() : Path);
}

public enum TrashState { DeletePending, Deleted, RestorePending, Restored, Cancelled }
public sealed record TrashEntry(int Version, Guid OperationId, TrashSource Source, OrderRecord Record,
    DateTimeOffset DeletedAt, TrashState State, DateTimeOffset? ConfirmedAt = null)
{
    // Старые архивы без поля сохраняют прежние 30 дней.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RetentionDays { get; init; }
    [JsonIgnore] public int RetentionPeriodDays => RetentionDays ?? OrderTrashService.RetentionDays;
    public void Validate()
    {
        if (Version != 1 || OperationId == Guid.Empty || !Enum.IsDefined(State) || Source is null || Record is null || DeletedAt == default)
            throw new InvalidDataException("Неподдерживаемая запись корзины. Архив не изменён.");
        Source.Validate(); Record.Validate();
        if (!OrderTrashService.IsSupportedRetention(RetentionPeriodDays))
            throw new InvalidDataException("Неизвестный срок хранения. Архив не изменён.");
    }
}

public interface ITrashStore
{
    Task<ITrashLease> OpenAsync(TrashSource source, CancellationToken cancellationToken = default);
}

public interface ITrashLease : IDisposable
{
    Task<IReadOnlyList<TrashEntry>> ReadAsync();
    Task SaveAsync(TrashEntry entry);
    Task RemoveAsync(Guid id);
}

public enum TrashDeleteOutcome { Deleted, Failed, NotAttempted }
public sealed record TrashDeleteItem(OrderRecord Record, TrashDeleteOutcome Outcome, Exception? Error = null);
public sealed record TrashDeleteResult(IReadOnlyList<TrashDeleteItem> Items)
{
    public bool IsComplete => Items.All(item => item.Outcome == TrashDeleteOutcome.Deleted);
    public Exception? Error => Items.FirstOrDefault(item => item.Outcome == TrashDeleteOutcome.Failed)?.Error;
}

public sealed record TrashPurgeResult(IReadOnlyList<Guid> RemovedIds, IReadOnlyList<TrashEntry>? Remaining, Exception? Error);

public sealed class OrderTrashService(IOrderMaintenance maintenance, TrashSource source, ITrashStore store, TimeProvider? clock = null,
    int retentionDays = 30)
{
    public const int RetentionDays = 30;
    public static bool IsSupportedRetention(int days) => days is 30 or 60 or 90;
    public int RetentionPeriodDays { get; } = IsSupportedRetention(retentionDays) ? retentionDays : RetentionDays;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TrashEntry> DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default)
    {
        expected.Validate(); source.Validate();
        using var lease = await store.OpenAsync(source, cancellationToken);
        var existing = await lease.ReadAsync();
        return await DeleteWithLeaseAsync(lease, expected, PendingIds(existing), cancellationToken);
    }

    public async Task<TrashDeleteResult> DeleteManyAsync(IReadOnlyCollection<OrderRecord> expected,
        Action<TrashDeleteItem>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var records = expected.ToArray();
        if (records.Length == 0 || records.Select(row => row.Id).Distinct().Count() != records.Length)
            throw new ArgumentException("Выберите непустой список заявок без повторов.", nameof(expected));
        // Проверка всей группы до первого DELETE. Каждая заявка остаётся самостоятельной операцией.
        foreach (var record in records) record.Validate();
        source.Validate();
        var results = new List<TrashDeleteItem>(records.Length);
        using var lease = await store.OpenAsync(source, cancellationToken);
        var pending = PendingIds(await lease.ReadAsync());
        var stopped = false;
        foreach (var record in records)
        {
            if (stopped)
            {
                results.Add(new(record, TrashDeleteOutcome.NotAttempted));
                continue;
            }
            TrashDeleteItem result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteWithLeaseAsync(lease, record, pending, cancellationToken);
                result = new(record, TrashDeleteOutcome.Deleted);
            }
            catch (Exception exception) { result = new(record, TrashDeleteOutcome.Failed, exception); stopped = true; }
            results.Add(result);
            // Ошибка отображения прогресса не должна менять подтверждённый результат записи.
            try { progress?.Invoke(result); }
            catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.Trash, exception); }
        }
        return new(results);
    }

    private static HashSet<int> PendingIds(IReadOnlyList<TrashEntry> entries) =>
        entries.Where(entry => entry.State is TrashState.DeletePending or TrashState.RestorePending).Select(entry => entry.Record.Id).ToHashSet();

    private async Task<TrashEntry> DeleteWithLeaseAsync(ITrashLease lease, OrderRecord expected, HashSet<int> pending, CancellationToken cancellationToken)
    {
        if (pending.Contains(expected.Id))
            throw new OrderWriteException(OrderWriteFailure.Conflict, "Для этой заявки есть неподтверждённая операция. Обновите список и корзину перед повтором.");
        var entry = new TrashEntry(1, Guid.NewGuid(), source, expected, _clock.GetUtcNow(), TrashState.DeletePending)
            { RetentionDays = RetentionPeriodDays };
        try { await lease.SaveAsync(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new OrderWriteException(OrderWriteFailure.NotSaved, "Не удалось надёжно сохранить копию в корзине. Заявка из базы не удалялась.", exception); }
        try { await maintenance.DeleteAsync(expected, cancellationToken); }
        catch (OrderWriteException exception) when (exception.Failure != OrderWriteFailure.OutcomeUnknown)
        {
            await TryMarkAsync(lease, entry with { State = TrashState.Cancelled });
            throw;
        }
        catch (Exception exception)
        {
            throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                "Удаление не подтверждено. Копия сохранена в корзине. Обновите список (F5) и корзину, чтобы проверить результат.", exception);
        }
        entry = entry with { State = TrashState.Deleted, ConfirmedAt = _clock.GetUtcNow() };
        try { await lease.SaveAsync(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Заявка удалена, копия сохранена; не удалось отметить завершение в корзине. Обновите список и корзину.", exception); }
        return entry;
    }

    public async Task<OrderRecord> RestoreAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        using var lease = await store.OpenAsync(source, cancellationToken);
        var entry = (await lease.ReadAsync()).SingleOrDefault(item => item.OperationId == operationId)
            ?? throw new OrderWriteException(OrderWriteFailure.NotSaved, "Архивная копия уже удалена. Обновите корзину.");
        if (entry.State != TrashState.Deleted)
            throw new OrderWriteException(OrderWriteFailure.Conflict, "Сначала обновите корзину и проверьте неподтверждённую операцию.");
        // Состояние записывается до INSERT; неизвестный commit можно безопасно сверить по всем полям.
        await lease.SaveAsync(entry with { State = TrashState.RestorePending });
        OrderRecord restored;
        try { restored = await maintenance.RestoreAsync(entry.Record, cancellationToken); }
        catch (OrderWriteException exception) when (exception.Failure != OrderWriteFailure.OutcomeUnknown)
        {
            await TryMarkAsync(lease, entry);
            throw;
        }
        catch (Exception exception)
        { throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Восстановление не подтверждено. Обновите список и корзину; копия сохранена.", exception); }
        if (restored != entry.Record)
            throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Не подтверждена точность восстановления. Обновите список и корзину; архив сохранён.");
        try { await lease.SaveAsync(entry with { State = TrashState.Restored }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Запись восстановлена; отметка корзины не подтверждена. Обновите список и корзину.", exception); }
        return restored;
    }

    public async Task<IReadOnlyList<TrashEntry>> RefreshAsync(bool reconcile, CancellationToken cancellationToken = default)
    {
        using var lease = await store.OpenAsync(source, cancellationToken);
        var entries = await lease.ReadAsync();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = entry;
            if (reconcile && entry.State is TrashState.DeletePending or TrashState.RestorePending)
            {
                var row = await maintenance.ReadRecordAsync(entry.Record.Id, cancellationToken);
                var state = entry.State == TrashState.DeletePending
                    ? row is null ? TrashState.Deleted : row == entry.Record ? TrashState.Cancelled : entry.State
                    : row == entry.Record ? TrashState.Restored : row is null ? TrashState.Deleted : entry.State;
                if (state != entry.State)
                {
                    current = entry with { State = state, ConfirmedAt = state == TrashState.Deleted ? _clock.GetUtcNow() : entry.ConfirmedAt };
                    await lease.SaveAsync(current);
                }
            }
            // Пока исход не подтверждён, никакой срок хранения не разрешает уничтожать снимок.
            if (current.State is TrashState.Deleted or TrashState.Restored or TrashState.Cancelled &&
                _clock.GetUtcNow() >= (current.ConfirmedAt ?? current.DeletedAt) &&
                _clock.GetUtcNow() - (current.ConfirmedAt ?? current.DeletedAt) >= TimeSpan.FromDays(current.RetentionPeriodDays))
                await lease.RemoveAsync(current.OperationId);
        }
        return (await lease.ReadAsync()).Where(entry => entry.State is TrashState.Deleted or TrashState.DeletePending or TrashState.RestorePending)
            .OrderByDescending(entry => entry.DeletedAt).ToArray();
    }

    public async Task PurgeAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        using var lease = await store.OpenAsync(source, cancellationToken);
        var entry = (await lease.ReadAsync()).SingleOrDefault(item => item.OperationId == operationId);
        if (entry is null) return;
        if (entry.State is TrashState.DeletePending or TrashState.RestorePending)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Неподтверждённую копию нельзя удалить. Сначала обновите корзину и проверьте запись.");
        await lease.RemoveAsync(operationId);
    }

    public async Task<TrashPurgeResult> PurgeManyAsync(IReadOnlyCollection<TrashEntry> expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var snapshot = expected.ToArray();
        if (snapshot.Length == 0 || snapshot.Select(entry => entry.OperationId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Укажите непустой список копий без повторов.", nameof(expected));
        source.Validate();
        foreach (var entry in snapshot)
        {
            entry.Validate();
            if (entry.Source.Key != source.Key || entry.State != TrashState.Deleted)
                throw new ArgumentException("Удалять можно только подтверждённые копии текущего источника.", nameof(expected));
        }
        using var lease = await store.OpenAsync(source, cancellationToken);
        var current = (await lease.ReadAsync()).ToDictionary(entry => entry.OperationId);
        // Проверяем всю согласованную группу до первого удаления под одной блокировкой.
        // Копии, добавленные после подтверждения, в группу не включаются.
        if (snapshot.Any(entry => !current.TryGetValue(entry.OperationId, out var actual) || actual != entry))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Корзина изменилась. Обновите её и подтвердите очистку заново.");
        var removed = new List<Guid>(snapshot.Length);
        Exception? error = null;
        foreach (var entry in snapshot)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await lease.RemoveAsync(entry.OperationId);
                removed.Add(entry.OperationId);
            }
            catch (Exception exception) { error = exception; break; }
        }
        IReadOnlyList<TrashEntry>? remaining = null;
        try
        {
            remaining = (await lease.ReadAsync()).Where(entry => entry.State is TrashState.Deleted or TrashState.DeletePending or TrashState.RestorePending)
                .OrderByDescending(entry => entry.DeletedAt).ToArray();
        }
        catch (Exception exception) { error ??= exception; }
        return new(removed, remaining, error);
    }

    private static async Task TryMarkAsync(ITrashLease lease, TrashEntry entry)
    {
        try { await lease.SaveAsync(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AppDiagnostics.Capture(DiagnosticOperation.Trash, exception); }
    }
}
