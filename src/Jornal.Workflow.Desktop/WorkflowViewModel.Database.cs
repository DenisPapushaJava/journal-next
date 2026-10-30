using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private readonly IWorkflowDatabaseService _databases;
    private readonly Dictionary<string, WorkflowRestartDraft> _sourceLetters = [];
    private CancellationTokenSource? _databaseSearch;
    private long _searchGeneration;
    private bool _readingDatabase;
    public string WorkspacePath => _store.FilePath;
    public Guid? DatabaseId => _store.DatabaseId;
    public bool IsMdb => _store is WorkflowMdbStore;
    public bool CanConnectDatabase => _databases.IsAvailable && CanUse;
    public bool RequiresDatabaseRefresh => _store is WorkflowMdbStore { RequiresRefresh: true };
    public string DatabaseDescription => IsMdb ? $"Общая база программы «{WorkflowIdentity.Name}». Другие компьютеры подключаются к этому же файлу MDB." :
        $"Локальные данные прототипа. На Windows можно создать общую MDB или подключить существующую базу программы «{WorkflowIdentity.Name}».";
    public bool HasMoreOrders => !RequiresDatabaseRefresh && _store is IWorkflowQueryStore { HasMore: true };
    private string SourceKey => _store.DatabaseId?.ToString("N") ?? "local";
    public async Task<bool> RestoreRestartDraftAsync(WorkflowRestartDraft draft, CancellationToken token = default)
    {
        if (draft.DatabaseId != _store.DatabaseId) return false;
        if (_store is not WorkflowMdbStore store) { RestoreRestartDraft(draft); return true; }
        var restored = false;
        await RunAsync(async () =>
        {
            var ids = (draft.Results?.Select(r => r.OrderId) ?? []).Concat(draft.OrderId is { } open ? new[] { open } : []).Distinct().ToArray();
            _workspace = await store.ReadOrdersAsync(ids, token);
            RefreshEmployees(_connection.EmployeeId); RefreshOrders(); NotifyPriceCatalog();
            RestoreRestartDraft(draft); restored = true;
        });
        return restored;
    }
    private void NotifyDatabase()
    {
        NotifyResultState();
        foreach (var name in new[] { nameof(WorkspacePath), nameof(IsMdb), nameof(CanConnectDatabase), nameof(DatabaseDescription),
            nameof(HasMoreOrders), nameof(RequiresDatabaseRefresh) }) OnPropertyChanged(name);
        NotifyOrderList(); OnPropertyChanged(nameof(TrashCaption));
        NotifyCuttingTask();
    }
    public async Task<string?> ConnectDatabaseAsync(string path, bool create, CancellationToken token = default)
    {
        if (!CanConnectDatabase) return "Создание и подключение MDB доступны на Windows. Дождитесь завершения операций.";
        if (HasPendingEdits) return "Сохраните или отмените черновики перед сменой базы данных.";
        if (RequiresDatabaseRefresh) return "Сначала обновите журнал (F5) и проверьте исход предыдущей записи.";
        CancelDatabaseSearch(); var switched = false;
        await RunAsync(async () =>
        {
            var previous = CreateRestartDraft(); var previousKey = SourceKey; var previousConnection = _connection;
            var candidate = await _databases.OpenAsync(path, create, token);
            var key = candidate.DatabaseId?.ToString("N") ?? throw new InvalidDataException("Не передан ID базы.");
            _sourceLetters.TryGetValue(key, out var letters);
            if (key == previousKey) letters = previous;
            if (candidate is IWorkflowQueryStore query) query.Query = new(RetainIds: (letters?.Results?.Select(r => r.OrderId) ?? [])
                .Concat(letters?.OrderId is { } opened ? new[] { opened } : []).Distinct().ToArray());
            var workspace = await candidate.ReadAsync(token);
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                var candidateConnection = new WorkflowConnection(candidate);
                if (!previousConnection.Matches(current) && !candidateConnection.Matches(current))
                    throw new WorkflowValidationException("Подключение изменено в другом окне. Обновите журнал перед сменой базы.");
                var employees = new Dictionary<string, Guid?>(current.DatabaseEmployees ?? []);
                employees[previousKey] = previousConnection.EmployeeId;
                var employee = candidateConnection.Matches(current) ? current.EmployeeId : employees.GetValueOrDefault(key);
                return current with
                {
                    DatabasePath = candidate.FilePath,
                    DatabaseId = candidate.DatabaseId,
                    EmployeeId = employee,
                    DatabaseEmployees = employees
                };
            }), token);
            _sourceLetters[previousKey] = previous;
            ForgetOpenCard(); IsCreating = false; NewCustomer = ""; NewManager = "";
            _reportReview = null; _reportImportOrderId = null;
            ResetCuttingTask(); _bendingFolderFailures = null;
            _cards.Clear(); SelectedOrder = null; _connection = new(candidate, saved.EmployeeId); _workspace = workspace;
            _localPreferences = saved; _hasLoadedOrders = true; RecordListedSnapshot(workspace);
            _refreshing = true;
            try { Search = ""; FilterDate = null; } finally { _refreshing = false; }
            RefreshEmployees(saved.EmployeeId); Technologist = CurrentEmployee; NotifyEntrySuggestions();
            RefreshOrders(); NotifyPriceCatalog(); NotifyDatabase();
            if (letters is not null) RestoreRestartDraft(letters);
            switched = true;
            await RememberStartupProfileAsync();
        }, "Не удалось завершить подключение MDB. Проверьте файл и локальные настройки. Если новая база уже появилась в выбранной папке, используйте «Подключить БД…».");
        return switched ? null : Error ?? "Подключение не изменено. Проверьте файл и повторите выбор.";
    }
    private void ConfigureDatabaseQuery()
    {
        if (_store is not IWorkflowQueryStore store) return;
        DateTimeOffset? from = FilterDate is { } day ? new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Local)) : null;
        DateTimeOffset? until = FilterDate is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date.AddDays(1), DateTimeKind.Local)) : null;
        var retain = _cards.Where(p => p.Value.HasEdits || p.Value.Result.Length > 0).Select(p => p.Key)
            .Concat(_openOrderId is { } id ? new[] { id } : []).Distinct().ToArray();
        store.Query = new(from, until, Search, RetainIds: retain);
    }
    private void CancelDatabaseSearch()
    {
        CancelBackgroundRefresh(); _searchPending = false;
        ++_searchGeneration;
        var cancellation = _databaseSearch; _databaseSearch = null;
        cancellation?.Cancel(); cancellation?.Dispose();
    }
    private void ScheduleDatabaseSearch()
    {
        if (_store is not IWorkflowQueryStore) return;
        CancelDatabaseSearch(); ConfigureDatabaseQuery(); NotifyDatabase(); _databaseSearch = new(); _searchPending = true;
        _ = SearchDatabaseAsync(_databaseSearch.Token, _searchGeneration, false);
    }
    public async Task SearchDatabaseNowAsync()
    {
        if (_store is not IWorkflowQueryStore) return;
        CancelDatabaseSearch(); ConfigureDatabaseQuery(); NotifyDatabase(); _databaseSearch = new(); _searchPending = true;
        await SearchDatabaseAsync(_databaseSearch.Token, _searchGeneration, true);
    }
    private async Task SearchDatabaseAsync(CancellationToken token, long generation, bool immediate)
    {
        try
        {
            if (!immediate) await Task.Delay(300, token);
            // A search never races a write or source change. A newer search cancels this pending read.
            while (!CanUse) await Task.Delay(50, token);
            if (generation != _searchGeneration || RequiresDatabaseRefresh) return;
            await ReadDatabasePageAsync(false, token, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _searchGeneration) Error = ex is WorkflowValidationException or WorkflowConflictException ? ex.Message : "Не удалось прочитать заявки. Проверьте доступ к MDB и обновите журнал.";
        }
        finally { if (generation == _searchGeneration) _searchPending = false; }
    }
    [RelayCommand]
    private async Task LoadMoreOrdersAsync()
    {
        if (!CanUse || !HasMoreOrders) return;
        CancelDatabaseSearch();
        await RunAsync(() => ReadDatabasePageAsync(true, CancellationToken.None, _searchGeneration));
    }
    private async Task ReadDatabasePageAsync(bool more, CancellationToken token, long generation)
    {
        if (_store is not IWorkflowQueryStore store) return;
        _readingDatabase = true; OnPropertyChanged(nameof(CanUse)); SaveSpecificationCommand.NotifyCanExecuteChanged(); NotifyOrderList(); NotifyCuttingTask();
        try
        {
            ConfigureDatabaseQuery();
            var source = _store;
            var book = more ? await store.ReadMoreAsync(token) : await store.ReadAsync(token);
            if (generation != _searchGeneration || !ReferenceEquals(source, _store)) return;
            CaptureCard(); var state = _openOrderId is { } id ? _cards.GetValueOrDefault(id) : null;
            _loadedOrderSnapshot = CurrentOrder; _workspace = book; _hasLoadedOrders = true; if (!more) RecordListedSnapshot(book);
            RefreshEmployees(CurrentEmployee?.Id); RefreshOrders(preserveRows: true); NotifyPriceCatalog();
            if (!IsCreating && CurrentOrder is { } order)
            { LoadOrder(order); if (state is not null) RestoreCard(order, state); }
            NotifyDatabase();
            await RememberStartupProfileAsync();
        }
        finally { _readingDatabase = false; OnPropertyChanged(nameof(CanUse)); SaveSpecificationCommand.NotifyCanExecuteChanged(); NotifyOrderList(); NotifyCuttingTask(); NotifyResultState(); }
    }
}
