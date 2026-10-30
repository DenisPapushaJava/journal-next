using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// An unaccepted read owns no changes in the live store (including its cursor/catalogue).
public sealed class WorkflowBackgroundRead
{
    internal WorkflowMdbStore Owner { get; }
    internal WorkflowWorkspace Baseline { get; }
    internal WorkflowMdbStore Candidate { get; }
    public WorkflowWorkspace Workspace { get; }
    internal WorkflowBackgroundRead(WorkflowMdbStore owner, WorkflowWorkspace baseline, WorkflowMdbStore candidate, WorkflowWorkspace workspace)
    { Owner = owner; Baseline = baseline; Candidate = candidate; Workspace = workspace; }
}

public sealed partial class WorkflowMdbStore
{
    public async Task<long> ReadBackgroundRevisionAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireBackgroundRead();
            var result = await Send(WorkflowAccessOperation.ReadRevision, token).ConfigureAwait(false);
            ValidateResponse(result);
            if (result.Revision < _snapshot.Version)
                throw new WorkflowValidationException("Версия MDB изменилась назад. Проверьте источник и обновите журнал (F5).");
            return result.Revision;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkflowBackgroundRead> PrepareBackgroundReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireBackgroundRead();
            var baseline = _snapshot; var boundary = _page.Cursor;
            var query = _query with { RetainIds = _query.RetainIds?.ToArray() };
            var candidate = new WorkflowMdbStore(FilePath, _transport, DatabaseId)
            { _inspected = true, _catalog = _catalog, _catalogVersion = _catalogVersion, _query = query, _page = new(query) };
            var book = await candidate.ReadAsync(token).ConfigureAwait(false);
            var revision = book.Version;
            // Refresh the already loaded range, including new rows inserted above its old end.
            while (candidate.HasMore && boundary is { } end && candidate._page.Cursor is { } cursor &&
                (cursor.Time > end.Time || cursor.Time == end.Time && cursor.Number > end.Number))
            {
                book = await candidate.ReadMoreAsync(token).ConfigureAwait(false);
                if (book.Version != revision) throw new WorkflowConflictException();
            }
            if (book.Version < baseline.Version) throw new WorkflowConflictException();
            token.ThrowIfCancellationRequested();
            var previous = baseline.Orders.ToDictionary(o => o.Id);
            // Native IPC creates new arrays even for equal records. Reuse equal rows so
            // another order's save does not invalidate every visible row binding.
            book = book with { Orders = book.Orders.Select(o => previous.TryGetValue(o.Id, out var old) && SameOrder(old, o) ? old : o).ToArray() };
            candidate._snapshot = book;
            return new(this, baseline, candidate, book);
        }
        finally { _gate.Release(); }
    }

    public bool TryAcceptBackgroundRead(WorkflowBackgroundRead read)
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            if (RequiresRefresh || !ReferenceEquals(read.Owner, this) || !ReferenceEquals(_snapshot, read.Baseline) ||
                !_page.Matches(read.Candidate.Query) || !(Query.RetainIds ?? []).SequenceEqual(read.Candidate.Query.RetainIds ?? [])) return false;
            var candidate = read.Candidate;
            _snapshot = read.Workspace; _catalog = candidate._catalog; _catalogVersion = candidate._catalogVersion;
            _page = candidate._page; TrashCount = candidate.TrashCount;
            return true;
        }
        finally { _gate.Release(); }
    }

    private void RequireBackgroundRead()
    {
        if (!_inspected || RequiresRefresh)
            throw new WorkflowValidationException("Сначала проверьте подключение и результат последней записи через F5.");
    }
}
