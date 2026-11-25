using System.Security.Cryptography;
using System.Text;
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
    public bool ShowShopTransferNotice => HasShopTransfer && ShopTransfer is { ShowNotice: true };
    partial void OnShopTransferChanging(WorkflowShopTransferViewModel? value)
    {
        if (ShopTransfer is not { } old) return;
        old.PropertyChanged -= OnShopTransferNoticeChanged;
        old.Discard();
    }
    partial void OnShopTransferChanged(WorkflowShopTransferViewModel? value)
    { if (value is { } current) current.PropertyChanged += OnShopTransferNoticeChanged; NotifyCuttingTask(); }
    private void OnShopTransferNoticeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(WorkflowShopTransferViewModel.ShowNotice)) OnPropertyChanged(nameof(ShowShopTransferNotice)); }
    private void ResetShopTransfer() => ShopTransfer = null;

    public async Task<WorkflowShopTransferViewModel?> PrepareShopTransferAsync(Func<string, Task> clipboard, bool reviewOnly = false)
    {
        var preview = CreateShopTransfer(clipboard);
        if (preview is null) return null;
        preview.ReviewOnly = reviewOnly;
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

    internal async Task<WorkflowShopTransferViewModel?> ContinueOfficeShopTransferAsync(WorkflowShopTransferViewModel preview,
        WorkflowOfficeShopReview request, bool reviewOnly)
    {
        if (!CanUse || !ReferenceEquals(ShopTransfer, preview) || !ReferenceEquals(preview.OfficeReview, request)) return null;
        CancelOfficeFileCheck();
        await RunAsync(async () =>
        {
            await preview.ContinueOfficeReviewAsync(request);
            if (!reviewOnly && preview.CanStartAutomatically && !preview.WasCancelled && ReferenceEquals(ShopTransfer, preview))
                await preview.CopyAsync();
        });
        return ReferenceEquals(ShopTransfer, preview) ? preview : null;
    }

    public WorkflowShopTransferViewModel? CreateShopTransfer(Func<string, Task> clipboard)
    {
        if (!CanCopyCuttingTask || CurrentOrder is not { } order) return null;
        if (AuthorProblem is { } problem) { Error = problem; return null; }
        if (IsRemoteStorage && _connection.RemoteClient is null) { Error = "Подключитесь к офисному серверу заново."; return null; }
        _cuttingTaskSource = _store; _cuttingTaskOrderId = order.Id; NotifyCuttingTask();
        if (!TaskPreview.IsReady || IsCuttingTaskTextStale || string.IsNullOrWhiteSpace(CuttingTaskText)) return null;
        if (ShopRoot is not { } root) { Error = "Укажите папку заданий в цех: Настройки → Хранилище."; return null; }
        Error = null;
        var source = _store; var snapshot = _workspace; var epoch = _cuttingTaskEpoch; var text = TaskPreview.Text;
        var remote = _connection.RemoteClient;
        var copiesRoot = OfficeCopiesRoot;
        var expectedMarks = WorkflowHandoffClearExpected.From(order);
        var mapPreferences = HansMapPreferences;
        var mapBundle = !mapPreferences.Combine ? ShopMapBundle.Separate : mapPreferences.AsPdf ? ShopMapBundle.Pdf : ShopMapBundle.Doc;
        var actor = ActorId();
        var sourceId = source.DatabaseId?.ToString("D") ?? Path.GetFullPath(source.FilePath);
        var paths = WorkflowShopFolderPaths.Plan(root, order, snapshot.Employees, snapshot.Customers, snapshot.Machines);
        async Task Validate(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_cuttingTaskDetached || epoch != _cuttingTaskEpoch || !ReferenceEquals(source, _store) || ShopRoot != root || HansMapPreferences != mapPreferences ||
                    CurrentOrder?.Id != order.Id || CurrentOrder.Version != order.Version || PrepareCuttingTask() is not { IsReady: true } current || current.Text != text)
                    throw new WorkflowValidationException("Заявка или настройки изменились. Закройте предпросмотр и подготовьте задание заново.");
            });
            var fresh = await source.ReadOrderAsync(order.Id, token);
            var latest = fresh.Orders.SingleOrDefault(o => o.Id == order.Id);
            if (!fresh.Employees.Any(e => e.Id == actor && e.CanWork))
                throw new WorkflowValidationException("Выберите действующего технолога перед передачей в цех.");
            if (latest?.Version != order.Version || latest.IsDeleted || !expectedMarks.Matches(latest) ||
                WorkflowCuttingTask.Prepare(latest, fresh.Employees, fresh.Customers, fresh.Machines).Text != text ||
                !WorkflowShopFolderPaths.Plan(root, latest, fresh.Employees, fresh.Customers, fresh.Machines).SequenceEqual(paths) ||
                order.ActiveProjects.Any(p => fresh.Machines.SingleOrDefault(m => m.Id == p.MachineId) != snapshot.Machines.SingleOrDefault(m => m.Id == p.MachineId)))
                throw new WorkflowValidationException("Заявка или справочники изменены другим пользователем. Закройте предпросмотр и обновите журнал (F5).");
            if (remote is not null && await Task.Run(() => remote.ReadCalculationDraft(order.Id), token) is { } pending)
            {
                var included = CalculationProjectIds(latest);
                if (pending.Entries.Any(entry => included.Contains(entry.ProjectId) &&
                    !latest.Specifications.Any(saved => saved.Id == entry.ProjectId && entry.Matches(saved.Current))))
                    throw new WorkflowValidationException("Есть незавершённый расчёт. Нажмите «Рассчитать», чтобы сверить его с офисом перед передачей в цех.");
            }
            if (remote is not null && !await Task.Run(() => Directory.Exists(root), token))
                throw new WorkflowValidationException("Папка заданий в цех на этом компьютере недоступна. Проверьте локальную копию Яндекс Диска в настройках.");
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
            expectedMarks = WorkflowHandoffClearExpected.From(saved.Orders.Single(o => o.Id == order.Id));
            RefreshOrders(preserveRows: true); NotifyOrder();
        }
        async Task RecordProjects(Guid[] projectIds, DateTimeOffset at, CancellationToken token)
        {
            var saved = await source.RecordProjectHandoffsAsync(order.Id, order.Version, projectIds, actor, at, token);
            if (_cuttingTaskDetached || epoch != _cuttingTaskEpoch || !ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id ||
                CurrentOrder.Version != order.Version || ShopRoot != root) throw new WorkflowValidationException("Отметки проектов сохранены. Обновите журнал и проверьте результат.");
            _workspace = saved;
            expectedMarks = WorkflowHandoffClearExpected.From(saved.Orders.Single(o => o.Id == order.Id));
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
        Task<IWorkflowShopPublication> OpenPublication(CancellationToken token) => Task.Run(async () =>
        {
            var profile = _preferences.Update(current => current.ShopPublicationId is not null ? current :
                current with { ShopPublicationId = Guid.NewGuid() }).ShopPublicationId!.Value;
            var folder = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
            var owner = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToString("N") + folder)).AsSpan(0, 16));
            if (remote is not null) return await remote.OpenShopPublicationAsync(order, owner, token);
            return WorkflowShopPublicationLock.Acquire(source.FilePath, source.DatabaseId!.Value, order.Id, owner);
        }, token);
        return new(root, sourceId, order, snapshot, Validate, CopyText, Transferred, Record, _wordMapEditor, RecordProjects, mapBundle)
        {
            PrepareOfficeSources = remote is null ? null :
                (approved, progress, token) => remote.PrepareShopSourcesAsync(order, snapshot, copiesRoot, progress, token, approved),
            OpenPublication = remote is not null || source.DatabaseId is not null ? OpenPublication : null
        };
    }
}
