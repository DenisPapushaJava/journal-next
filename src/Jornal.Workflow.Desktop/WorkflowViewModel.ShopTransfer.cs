using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasShopTransfer))]
    private WorkflowShopTransferViewModel? _shopTransfer;
    private long _shopTransferVersion;
    public bool HasShopTransfer => ShopTransfer is not null && IsCuttingTaskVisible && CurrentOrder?.Version == _shopTransferVersion && TaskPreview.IsReady;
    partial void OnShopTransferChanged(WorkflowShopTransferViewModel? value) => NotifyCuttingTask();
    private void ResetShopTransfer() { ShopTransfer?.Cancel(); ShopTransfer = null; }

    public async Task<WorkflowShopTransferViewModel?> PrepareShopTransferAsync(Func<string, Task> clipboard, bool reviewOnly = false)
    {
        var preview = CreateShopTransfer(clipboard);
        if (preview is null) return null;
        _shopTransferVersion = CurrentOrder!.Version;
        ShopTransfer = preview;
        await RunAsync(async () =>
        {
            await preview.RefreshAsync();
            if (!reviewOnly && preview.CanStartAutomatically && !preview.WasCancelled && ReferenceEquals(ShopTransfer, preview)) await preview.CopyAsync();
        });
        return ReferenceEquals(ShopTransfer, preview) ? preview : null;
    }

    public async Task RetryShopTransferTextAsync()
    {
        if (!CanUse || ShopTransfer is not { CanCopyText: true } preview) return;
        await RunAsync(() => preview.CopyTextCommand.ExecuteAsync(null));
    }

    public WorkflowShopTransferViewModel? CreateShopTransfer(Func<string, Task> clipboard)
    {
        if (!CanCopyCuttingTask || CurrentOrder is not { } order) return null;
        _cuttingTaskSource = _store; _cuttingTaskOrderId = order.Id; NotifyCuttingTask();
        if (!TaskPreview.IsReady || string.IsNullOrWhiteSpace(CuttingTaskText)) return null;
        if (ShopRoot is not { } root) { Error = "Укажите папку заданий в цех: Настройки → Хранилище."; return null; }
        Error = null;
        var source = _store; var snapshot = _workspace; var epoch = _cuttingTaskEpoch; var text = TaskPreview.Text;
        var actor = ActorId();
        var sourceId = source.DatabaseId?.ToString("D") ?? Path.GetFullPath(source.FilePath);
        var paths = WorkflowShopFolderPaths.Plan(root, order, snapshot.Employees, snapshot.Customers, snapshot.Machines);
        async Task Validate(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_cuttingTaskDetached || epoch != _cuttingTaskEpoch || !ReferenceEquals(source, _store) || ShopRoot != root ||
                    CurrentOrder?.Id != order.Id || CurrentOrder.Version != order.Version || PrepareCuttingTask() is not { IsReady: true } current || current.Text != text)
                    throw new WorkflowValidationException("Заявка или настройки изменились. Закройте предпросмотр и подготовьте задание заново.");
            });
            var fresh = await source.ReadOrderAsync(order.Id, token);
            var latest = fresh.Orders.SingleOrDefault(o => o.Id == order.Id);
            if (latest?.Version != order.Version || latest.IsDeleted ||
                WorkflowCuttingTask.Prepare(latest, fresh.Employees, fresh.Customers, fresh.Machines).Text != text ||
                !WorkflowShopFolderPaths.Plan(root, latest, fresh.Employees, fresh.Customers, fresh.Machines).SequenceEqual(paths) ||
                order.ActiveProjects.Any(p => fresh.Machines.SingleOrDefault(m => m.Id == p.MachineId) != snapshot.Machines.SingleOrDefault(m => m.Id == p.MachineId)))
                throw new WorkflowValidationException("Заявка или справочники изменены другим пользователем. Закройте предпросмотр и обновите журнал (F5).");
        }
        void Transferred(ShopTransferResult[] results)
        {
            if (_cuttingTaskDetached || !ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id || ShopRoot != root) return;
            NotifyShopFolderPaths(forceCheck: true);
            // The copy service has already verified these receipts. Enable successful rows immediately;
            // the new check supersedes any older in-flight "not found" result.
            foreach (var result in results.Where(r => r.Success))
                if (Projects.SingleOrDefault(r => r.Project.Id == result.ProjectId) is { } row && row.ShopFolderPath == paths.Single(p => p.ProjectId == result.ProjectId).Path)
                    row.ApplyShopFolderAvailability(ShopFolderAvailability.Available);
        }
        async Task Record(DateTimeOffset at, CancellationToken token)
        {
            var saved = await source.RecordShopHandoffAsync(order.Id, order.Version, actor, at, token);
            if (_cuttingTaskDetached || epoch != _cuttingTaskEpoch || !ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id ||
                CurrentOrder.Version != order.Version || ShopRoot != root) throw new WorkflowValidationException("Заявка или настройки изменились. Обновите журнал (F5) и проверьте отметку передачи.");
            _workspace = saved;
            RefreshOrders(preserveRows: true); NotifyOrder();
        }
        async Task CopyText(string value)
        {
            var text = CuttingTaskText;
            if (string.IsNullOrWhiteSpace(text) || !IsCurrentCuttingTaskCopy(source, order, epoch, value, text))
                throw new WorkflowValidationException("Проверьте текст задания перед копированием.");
            await clipboard(text);
            RememberCuttingTaskCopy(source, order, epoch, value, text);
        }
        return new(root, sourceId, order, snapshot, Validate, CopyText, Transferred, Record);
    }
}
