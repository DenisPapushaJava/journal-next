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
    private CancellationTokenSource? _officeFileCheck;
    public string WorkspacePath => _connection.Office?.Address ?? _store.FilePath;
    public Guid? DatabaseId => _store.DatabaseId ?? _connection.Office?.DatabaseId;
    public bool IsRemoteStorage => _connection.Office is not null;
    internal WorkflowOfficeStorageSettings? SavedOfficeStorage => _localPreferences.OfficeStorage;
    internal string? SavedOfficeAddress => SavedOfficeStorage?.Address;
    internal bool HasVerifiedOfficeStorage => !string.IsNullOrWhiteSpace(SavedOfficeStorage?.ZeroTierInterfaceId);
    internal WorkflowOfficeSetupDraft? SavedOfficeSetup => _localPreferences.OfficeSetup;

    internal async Task RememberOfficeSetupAsync(WorkflowOfficeSetupDraft draft, CancellationToken token)
    {
        if (_localPreferences.OfficeSetup == draft) return;
        var saved = await Task.Run(() => _preferences.Update(current => current.OfficeSetup == draft ? current :
            current with { OfficeSetup = draft }), token);
        _localPreferences = _localPreferences with { OfficeSetup = saved.OfficeSetup };
    }
    internal string? OfficeCopiesRoot => _localPreferences.OfficeCopiesRoot;
    public bool IsMdb => _store is WorkflowMdbStore && !IsRemoteStorage;
    public bool CanConnectDatabase => _databases.IsAvailable && CanUse;
    public bool RequiresDatabaseRefresh => _store is WorkflowMdbStore { RequiresRefresh: true };
    public string DatabaseDescription => IsRemoteStorage ? "База данных и общая папка проектов задаются на сервере." :
        IsMdb ? $"Общая база программы «{WorkflowIdentity.Name}». Другие компьютеры подключаются к этому же файлу MDB." :
        $"Локальные данные прототипа. На Windows можно создать общую MDB или подключить существующую базу программы «{WorkflowIdentity.Name}».";
    public bool HasMoreOrders => !RequiresDatabaseRefresh && _store is IWorkflowQueryStore { HasMore: true };
    private string SourceKey => DatabaseId?.ToString("N") ?? "local";

    internal async Task<WorkflowOfficeWorkingCopy?> OpenOfficeProjectAsync(WorkflowProjectRow row, CancellationToken token)
    {
        if (_connection.RemoteClient is not { } server || CurrentOrder is not { } order || !Projects.Contains(row)) return null;
        CancelOfficeFileCheck();
        WorkflowOfficeWorkingCopy? copy = null;
        var copiesRoot = OfficeCopiesRoot;
        var project = row.Project;
        await RunAsync(async () => copy = await Task.Run(() => server.OpenWorkingCopyAsync(order.Id, project.Id, token, copiesRoot, project.NativePath), token),
            "Не удалось обновить локальную папку проекта.");
        if (!ReferenceEquals(server, _connection.RemoteClient) || CurrentOrder?.Id != order.Id || !Projects.Contains(row)) return null;
        if (copy?.HasPendingFiles is { } pending) row.ApplyPendingOfficeFiles(pending);
        return copy;
    }

    internal async Task<WorkflowOfficeFileComparison?> CompareOfficeWorkingFileAsync(WorkflowProjectRow row,
        WorkflowOfficeFileConflict conflict, CancellationToken token)
    {
        if (_connection.RemoteClient is not { } server || CurrentOrder is not { } order || !Projects.Contains(row)) return null;
        CancelOfficeFileCheck();
        WorkflowOfficeFileComparison? comparison = null;
        await RunAsync(async () => comparison = await server.CompareWorkingFileAsync(order.Id, row.Project.Id, conflict, token),
            "Не удалось скачать версии файла для сравнения.");
        return ReferenceEquals(server, _connection.RemoteClient) && Projects.Contains(row) ? comparison : null;
    }

    internal async Task<bool> ChooseOfficeWorkingFileAsync(WorkflowProjectRow row, WorkflowOfficeFileConflict conflict,
        WorkflowOfficeFileComparison comparison, bool useOffice, CancellationToken token)
    {
        if (_connection.RemoteClient is not { } server || CurrentOrder is not { } order || !Projects.Contains(row)) return false;
        CancelOfficeFileCheck();
        var chosen = false;
        await RunAsync(async () =>
        {
            await server.ChooseWorkingFileAsync(order.Id, row.Project.Id, conflict, comparison, useOffice, token);
            chosen = true;
        }, "Не удалось применить выбранную версию файла.");
        return chosen && ReferenceEquals(server, _connection.RemoteClient) && Projects.Contains(row);
    }

    private void CancelOfficeFileCheck() => _officeFileCheck?.Cancel();

    internal async Task RefreshOfficeFileStatesAsync(CancellationToken token = default)
    {
        CancelOfficeFileCheck();
        if (_cuttingTaskDetached || _connection.RemoteClient is not { } server || CurrentOrder is not { } order) return;
        var projects = Projects.Where(row => row.CanPublishFiles).ToArray();
        if (projects.Length == 0) return;
        var copiesRoot = OfficeCopiesRoot;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
        _officeFileCheck = check;
        try
        {
            foreach (var row in projects)
            {
                bool? pending;
                try
                {
                    pending = await Task.Run(() => server.HasPendingFilesAsync(row.Project.Id, copiesRoot, row.Project.NativePath, check.Token),
                        check.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException or
                    InvalidDataException or System.Text.Json.JsonException)
                { continue; }
                if (check.IsCancellationRequested || _cuttingTaskDetached || !ReferenceEquals(server, _connection.RemoteClient) ||
                    CurrentOrder?.Id != order.Id) return;
                if (pending is { } known && Projects.Contains(row)) row.ApplyPendingOfficeFiles(known);
            }
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_officeFileCheck, check)) _officeFileCheck = null; }
    }

    internal async Task<string?> ResolveSpecificationSourceAsync(string path, CancellationToken token)
    {
        if (!IsRemoteStorage) return path;
        if (_connection.RemoteClient is not { } server || CurrentOrder is not { } order || SelectedProject is not { } row) return null;
        string? local = null;
        await RunAsync(async () =>
        {
            var (folder, skipped, unavailable) = await server.OpenCalculationFolderAsync(order.Id, row.Project, OfficeCopiesRoot, token);
            var name = WorkflowOfficeProjectPaths.FileName(path);
            if (unavailable.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new WorkflowValidationException($"Спецификация «{name}» недоступна в офисной папке проекта.");
            if (skipped.Contains(name)) throw new WorkflowValidationException("Рабочая спецификация удалена в офисной папке проекта.");
            local = WorkflowOfficeProjectPaths.FileInProject(folder, name);
        }, "Не удалось открыть спецификацию из локальной копии проекта.");
        return local;
    }

    internal async Task<string?> PublishOfficeProjectAsync(WorkflowProjectRow row, CancellationToken token)
    {
        if (_connection.Office is { CanWrite: false }) { Error = "Удалённое сохранение выключено в панели сервера."; return null; }
        if (_connection.RemoteClient is not { } server || CurrentOrder is not { } order || !Projects.Contains(row)) return null;
        CancelOfficeFileCheck();
        int? sent = null;
        await RunAsync(async () => sent = await server.PublishCopyAsync(order.Id, row.Project.Id, token, OfficeCopiesRoot, row.Project.NativePath),
            "Не удалось завершить отправку файлов. Локальные копии сохранены.");
        await RefreshOfficeFileStatesAsync(token);
        return sent switch { null => null, 0 => "Изменённых файлов для отправки нет.", _ => $"Отправлено файлов: {sent}." };
    }

    internal async Task<string?> SaveOfficeCopiesRootAsync(string? expected, string? root, CancellationToken token)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                if (current.OfficeCopiesRoot == root) return current;
                if (current.OfficeCopiesRoot != expected)
                    throw new WorkflowValidationException("Папка для локальных копий изменена в другом окне. Откройте настройки заново.");
                return current with { OfficeCopiesRoot = root };
            }), token);
            _localPreferences = _localPreferences with { OfficeCopiesRoot = saved.OfficeCopiesRoot };
            return null;
        }
        catch (WorkflowValidationException ex) { return ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { return "Не удалось сохранить папку для локальных копий. Проверьте доступ к настройкам на этом компьютере."; }
    }

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
        foreach (var name in new[] { nameof(WorkspacePath), nameof(IsMdb), nameof(IsRemoteStorage), nameof(CanConnectDatabase), nameof(DatabaseDescription),
            nameof(HasMoreOrders), nameof(RequiresDatabaseRefresh) }) OnPropertyChanged(name);
        NotifyOrderList(); OnPropertyChanged(nameof(TrashCaption));
        NotifyCuttingTask();
    }
    public async Task<string?> ConnectDatabaseAsync(string path, bool create, CancellationToken token = default)
    {
        if (!CanConnectDatabase) return "Создание и подключение MDB доступны на Windows. Дождитесь завершения операций.";
        return await SwitchStorageAsync(async () => new(await _databases.OpenAsync(path, create, token)), token);
    }

    internal async Task<string?> ConnectOfficeStorageAsync(string? address, string? key, WorkflowOfficeRoute? route,
        CancellationToken token, Action? beginCommit = null)
    {
        var error = await SwitchStorageAsync(async () =>
        {
            var existing = _preferences.Load().OfficeStorage;
            var office = string.IsNullOrWhiteSpace(key) && existing is not null &&
                WorkflowOfficeTransport.RequireServer(address).AbsoluteUri == existing.Address
                    ? WorkflowOfficeStorageClient.Open(existing, _preferences.DirectoryPath, route)
                    : await WorkflowOfficeStorageClient.ConnectAsync(address, key, _preferences.DirectoryPath,
                        route ?? throw new WorkflowValidationException("Проверьте подключение к серверу заново."), token);
            return new(office.CreateStore(), Office: office.Settings, RemoteClient: office);
        }, token, beginCommit);
        if (error is not null) token.ThrowIfCancellationRequested();
        return error;
    }

    internal Task<string?> ReturnToLocalStorageAsync(CancellationToken token) => SwitchStorageAsync(() =>
    {
        var saved = _preferences.Load();
        IWorkflowWorkspaceStore local = saved.DatabasePath is { } path && _databases.IsAvailable
            ? new WorkflowMdbStore(path, databaseId: saved.DatabaseId)
            : new LocalWorkflowWorkspaceStore(Path.Combine(_preferences.DirectoryPath, Path.GetFileName(WorkflowIdentity.WorkspacePath)));
        return Task.FromResult(new WorkflowConnection(local));
    }, token);

    internal string? StorageSwitchProblem() => !CanUse ? "Дождитесь завершения текущей операции." :
        HasPendingEdits ? "Сохраните или отмените черновики перед сменой базы данных." :
        RequiresDatabaseRefresh ? "Сначала обновите журнал (F5) и проверьте исход предыдущей записи." : null;

    internal async Task<string?> SwitchStorageAsync(Func<Task<WorkflowConnection>> open, CancellationToken token,
        Action? beginCommit = null)
    {
        if (StorageSwitchProblem() is { } problem) return problem;
        CancelDatabaseSearch(); var switched = false;
        await RunAsync(async () =>
        {
            var previous = CreateRestartDraft(); var previousKey = SourceKey; var previousConnection = _connection;
            var candidateConnection = await open();
            var candidate = candidateConnection.Store;
            var key = candidate.DatabaseId?.ToString("N") ?? "local";
            _sourceLetters.TryGetValue(key, out var letters);
            if (key == previousKey) letters = previous;
            if (candidate is IWorkflowQueryStore query) query.Query = new(RetainIds: (letters?.Results?.Select(r => r.OrderId) ?? [])
                .Concat(letters?.OrderId is { } opened ? new[] { opened } : []).Distinct().ToArray());
            var workspace = await candidate.ReadAsync(token);
            key = candidate.DatabaseId?.ToString("N") ?? "local";
            token.ThrowIfCancellationRequested();
            beginCommit?.Invoke();
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                if (!previousConnection.Matches(current) && !candidateConnection.Matches(current))
                    throw new WorkflowValidationException("Подключение изменено в другом окне. Обновите журнал перед сменой базы.");
                var employees = new Dictionary<string, Guid?>(current.DatabaseEmployees ?? []);
                employees[previousKey] = previousConnection.EmployeeId;
                var employee = candidateConnection.Matches(current) ? current.EmployeeId : employees.GetValueOrDefault(key);
                if (candidateConnection.Office != current.OfficeStorage)
                    candidateConnection.RemoteClient?.SaveCredential();
                return current with
                {
                    OfficeStorage = candidateConnection.Office ?? current.OfficeStorage,
                    UseOfficeStorage = candidateConnection.Office is not null,
                    DatabasePath = candidateConnection.Office is not null ? current.DatabasePath :
                        candidate is WorkflowMdbStore ? candidate.FilePath : null,
                    DatabaseId = candidateConnection.Office is not null ? current.DatabaseId : candidate.DatabaseId,
                    EmployeeId = employee,
                    DatabaseEmployees = employees
                };
            }), token);
            _sourceLetters[previousKey] = previous;
            ForgetOpenCard(); IsCreating = false; NewCustomer = ""; NewManager = "";
            _reportReview = null; _reportImportOrderId = null;
            ResetCuttingTask(); _bendingFolderFailures = null;
            _cards.Clear(); SelectedOrder = null; _connection = candidateConnection with { EmployeeId = saved.EmployeeId }; _workspace = workspace;
            CancelManagerMapJobs();
            _localPreferences = saved; _hasLoadedOrders = true; RecordListedSnapshot(workspace);
            _refreshing = true;
            try { Search = ""; FilterDate = null; ResetQuickOrderFilterValues(); } finally { _refreshing = false; }
            RefreshEmployees(saved.EmployeeId); Technologist = CurrentEmployee; NotifyEntrySuggestions();
            RefreshOrders(); NotifyPriceCatalog(); NotifyDatabase();
            if (letters is not null) RestoreRestartDraft(letters);
            switched = true;
            await RememberStartupProfileAsync();
        }, "Не удалось подключиться. Проверьте доступ к хранилищу и локальным настройкам.");
        return switched ? null : Error ?? "Подключение не изменено. Проверьте источник и повторите попытку.";
    }
    private void ConfigureDatabaseQuery()
    {
        if (_store is not IWorkflowQueryStore store) return;
        DateTimeOffset? from = FilterDate is { } day ? new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Local)) : null;
        DateTimeOffset? until = PeriodUntil is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local)) : null;
        var retain = _cards.Where(p => p.Value.HasEdits || p.Value.Result.Length > 0 || p.Value.TaskEdit is not null).Select(p => p.Key)
            .Concat(_openOrderId is { } id ? new[] { id } : []).Distinct().ToArray();
        store.Query = new(from, until, Search, RetainIds: retain, Filter: CurrentOrderFilter);
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
        _orderQueryFailed = false;
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
            if (generation == _searchGeneration)
            { _orderQueryFailed = true; Error = ex is WorkflowValidationException or WorkflowConflictException ? ex.Message : "Не удалось прочитать заявки. Проверьте доступ к MDB и обновите журнал."; }
        }
        finally { if (generation == _searchGeneration) { _searchPending = false; NotifyOrderList(); } }
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
            _orderQueryFailed = false;
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
