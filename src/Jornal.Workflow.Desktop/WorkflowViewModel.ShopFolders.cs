using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private sealed record ShopFolderTarget(WorkflowProjectRow Row, string Path);
    private sealed record ShopFolderContext(IWorkflowWorkspaceStore Source, string SourceId, Guid OrderId, ShopFolderTarget[] Targets);
    private ShopFolderContext? _shopFolderContext;
    private CancellationTokenSource? _shopFolderCheck;
    private Task _shopFolderCheckTask = Task.CompletedTask;
    private readonly SemaphoreSlim _shopFolderReadGate = new(1, 1);
    private bool _shopFolderChecksDetached;
    public string? ShopRoot => _localPreferences.ShopRoot;
    internal async Task<string?> ShopFolderToOpenAsync(WorkflowProjectRow row, CancellationToken token)
    {
        if (!CanUse || !Projects.Contains(row) || !row.CanOpenShopFolder || row.ShopFolderPath is not { } path || CurrentOrder is not { } order) return null;
        var source = _store; var sourceId = source.DatabaseId?.ToString("D") ?? Path.GetFullPath(source.FilePath);
        string? result = null;
        await RunAsync(async () =>
        {
            bool owned;
            try { owned = await Task.Run(() => WorkflowShopTransfer.HasOwnedDestinationAsync(new(row.Project, path, [], null, null), sourceId, order.Id, token), token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { if (Projects.Contains(row) && row.ShopFolderPath == path) row.ApplyShopFolderAvailability(ShopFolderAvailability.Unavailable); throw; }
            if (!ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id || !Projects.Contains(row) || row.ShopFolderPath != path) return;
            if (owned) result = path;
            else { row.ApplyShopFolderAvailability(ShopFolderAvailability.NotFound); Error = "Папка задания в цехе ещё не создана. Нажмите «Отправить в работу…», чтобы подготовить файлы."; }
        }, "Папка задания в цехе недоступна. Проверьте путь и доступ к папке.");
        return result;
    }

    private void NotifyShopFolderPaths(bool forceCheck = false,
        Func<ShopProjectFiles, string, Guid, CancellationToken, Task<bool>>? reader = null)
    {
        foreach (var row in Projects)
        {
            string? path = null;
            if (IsCurrentOrderActive && ShopRoot is { } root && CurrentOrder is { IsDeleted: false } order && row is { IsDeleted: false, IsLegacyCalculation: false })
            {
                try { path = WorkflowShopFolderPaths.ForProject(root, order, row.Project.Id, _workspace.Employees, _workspace.Customers, _workspace.Machines).Path; }
                catch (Exception ex) when (ex is WorkflowValidationException or ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            row.ApplyShopFolderPath(path);
        }
        ScheduleShopFolderCheck(forceCheck, reader);
    }

    internal Task RefreshShopFolderAvailabilityAsync(Func<ShopProjectFiles, string, Guid, CancellationToken, Task<bool>>? reader = null)
    { NotifyShopFolderPaths(forceCheck: true, reader); return _shopFolderCheckTask; }

    private void ScheduleShopFolderCheck(bool force, Func<ShopProjectFiles, string, Guid, CancellationToken, Task<bool>>? reader)
    {
        if (_shopFolderChecksDetached) return;
        var targets = Projects.Where(row => row.ShopFolderPath is not null).Select(row => new ShopFolderTarget(row, row.ShopFolderPath!)).ToArray();
        var context = CurrentOrder is { } order && targets.Length > 0 ? new ShopFolderContext(_store,
            _store.DatabaseId?.ToString("D") ?? Path.GetFullPath(_store.FilePath), order.Id, targets) : null;
        if (!force && SameShopFolderContext(_shopFolderContext, context)) return;
        _shopFolderCheck?.Cancel(); _shopFolderContext = context;
        if (context is null) { _shopFolderCheck = null; _shopFolderCheckTask = Task.CompletedTask; return; }
        var request = new CancellationTokenSource(); _shopFolderCheck = request;
        foreach (var target in targets.Where(t => !t.Row.CanOpenShopFolder)) target.Row.ApplyShopFolderAvailability(ShopFolderAvailability.Checking);
        _shopFolderCheckTask = CheckShopFoldersAsync(context, request, reader ?? WorkflowShopTransfer.HasOwnedDestinationAsync);
    }

    private static bool SameShopFolderContext(ShopFolderContext? a, ShopFolderContext? b) => a is null && b is null || a is not null && b is not null &&
        ReferenceEquals(a.Source, b.Source) && a.SourceId == b.SourceId && a.OrderId == b.OrderId && a.Targets.SequenceEqual(b.Targets);

    private async Task CheckShopFoldersAsync(ShopFolderContext context, CancellationTokenSource request,
        Func<ShopProjectFiles, string, Guid, CancellationToken, Task<bool>> reader)
    {
        var token = request.Token; var entered = false;
        try
        {
            // A slow disconnected share must not create parallel filesystem probes on every card switch.
            await _shopFolderReadGate.WaitAsync(token); entered = true;
            var states = await Task.Run(async () =>
            {
                var result = new List<ShopFolderAvailability>();
                foreach (var target in context.Targets)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        result.Add(await reader(new(target.Row.Project, target.Path, [], null, null), context.SourceId, context.OrderId, token)
                        ? ShopFolderAvailability.Available : ShopFolderAvailability.NotFound);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { result.Add(ShopFolderAvailability.Unavailable); }
                }
                return result;
            }, token);
            if (token.IsCancellationRequested || _shopFolderChecksDetached || !ReferenceEquals(_shopFolderContext, context) ||
                !ReferenceEquals(_store, context.Source) || CurrentOrder?.Id != context.OrderId) return;
            for (var i = 0; i < context.Targets.Length; i++)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(_shopFolderContext, context)) break;
                if (Projects.Contains(context.Targets[i].Row) && context.Targets[i].Row.ShopFolderPath == context.Targets[i].Path)
                    context.Targets[i].Row.ApplyShopFolderAvailability(states[i]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (entered) _shopFolderReadGate.Release();
            if (ReferenceEquals(_shopFolderCheck, request)) _shopFolderCheck = null;
            request.Dispose();
        }
    }

    private void StopShopFolderChecks()
    {
        if (_shopFolderChecksDetached) return;
        _shopFolderChecksDetached = true; _shopFolderContext = null; _shopFolderCheck?.Cancel();
        _ = DisposeShopFolderGateAsync();
    }
    private async Task DisposeShopFolderGateAsync()
    { await _shopFolderReadGate.WaitAsync(); _shopFolderReadGate.Dispose(); }
    internal string ShopFolderPreview(string? root)
    {
        try
        {
            if (WorkflowShopFolderPaths.NormalizeRoot(root) is not { } path) return "Укажите начальную папку, чтобы увидеть полный путь.";
            if (CurrentOrder is not { } order) return "Для примера откройте заявку с проектами.";
            return string.Join(Environment.NewLine + Environment.NewLine,
                WorkflowShopFolderPaths.Plan(path, order, _workspace.Employees, _workspace.Customers, _workspace.Machines).Select(p => p.Path));
        }
        catch (Exception ex) when (ex is WorkflowValidationException or ArgumentException or NotSupportedException or PathTooLongException)
        { return ex is WorkflowValidationException ? ex.Message : "Проверьте полный путь к папке заданий в цех."; }
    }
    internal async Task<string?> SaveShopRootAsync(string? expected, string? root, CancellationToken token)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            root = WorkflowShopFolderPaths.NormalizeRoot(root);
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                if (current.ShopRoot != expected && current.ShopRoot != root)
                    throw new InvalidOperationException("Папка цеха изменена в другом окне. Ваш ввод сохранён; откройте настройки заново и сравните пути.");
                return current.ShopRoot == root ? current : current with { ShopRoot = root };
            }), token);
            _localPreferences = _localPreferences with { ShopRoot = saved.ShopRoot };
            OnPropertyChanged(nameof(ShopRoot));
            NotifyShopFolderPaths();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or
            ArgumentException or InvalidOperationException or WorkflowValidationException or NotSupportedException)
        {
            return ex is WorkflowValidationException or InvalidOperationException ? ex.Message :
                "Не удалось сохранить папку цеха на этом компьютере. Проверьте путь и доступ к локальным настройкам. Ваш ввод сохранён.";
        }
    }
}
