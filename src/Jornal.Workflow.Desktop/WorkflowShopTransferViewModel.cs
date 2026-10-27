using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowShopFileChoice(ShopFile file) : ObservableObject
{
    public ShopFile File { get; } = file;
    public string Name => File.Name;
    public string Detail => (File.Kind == ShopFileKind.Program ? "УП · " : "Карта · ") + File.Reason;
    public bool CanSelect => File.IsReadable;
    [ObservableProperty] private bool _isSelected = file.Suggested;
}

public partial class WorkflowShopProjectChoice : ObservableObject
{
    public ShopProjectFiles Plan { get; }
    public string Name => Plan.Project.Name;
    public string Destination => Plan.Destination;
    public WorkflowShopFileChoice[] Files { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDestination))] private string? _destinationRevision;
    public bool HasDestination => DestinationRevision is not null;
    private ShopTree? _destinationContents;
    public bool RequiresReplacement => _destinationContents is not null && !WorkflowShopTransfer.MatchesDestination(Selection, _destinationContents);
    public string? Problem { get; }
    public bool HasBending => Plan.Bending is not null;
    public string SelectionHelp => Problem is not null ? "" : Plan.ReviewReason ?? (Files.Length == 0 ?
        "Рядом с рабочим проектом пока нет УП и карт. Сохраните их в папку проекта и нажмите «Обновить подбор»." :
        !Files.Any(f => f.File.Kind == ShopFileKind.Program) ? "Не найдены программы резки. Сохраните УП рядом с рабочим проектом и обновите подбор." :
        !Files.Any(f => f.File.Kind == ShopFileKind.Map) ? "Не найдены карты раскроя. Сохраните карты рядом с рабочим проектом и обновите подбор." :
        Plan.Files.All(f => !f.Suggested) ? "Связи файлов не подтверждены автоматически. Откройте нужные УП и карты, отметьте их и подтвердите состав ниже." : "");
    public bool HasSelectionHelp => SelectionHelp.Length > 0;
    public string BendingText => Plan.Bending is { } tree ? $"ГИБКА — целиком: файлов {tree.Files.Length}, папок {tree.Directories.Length}. Включая скрытые файлы и пустые папки." : "";
    [ObservableProperty] private bool _reviewed;
    [ObservableProperty] private string? _result;
    public bool RequiresReview => Plan.NeedsReview || Files.Any(f => f.IsSelected != f.File.Suggested);
    public bool CanCopy => Problem is null && (!RequiresReview || Reviewed) && Files.Any(f => f.IsSelected && f.File.Kind == ShopFileKind.Program) &&
        Files.Any(f => f.IsSelected && f.File.Kind == ShopFileKind.Map) && !Files.Any(f => f.IsSelected && (!f.File.IsReadable || f.File.Length == 0));
    public string Summary => Result ?? Problem ?? (CanCopy ? "Комплект выбран" : "Проверьте УП и карты");
    public ShopTransferSelection Selection => new(Plan, Files.Where(f => f.IsSelected).Select(f => f.File).ToArray(), Reviewed, DestinationRevision);
    public WorkflowShopProjectChoice(ShopProjectFiles plan, string? revision, string? problem)
    {
        Plan = plan; DestinationRevision = revision; Problem = problem ?? plan.Problem;
        Files = plan.Files.Select(f => new WorkflowShopFileChoice(f)).ToArray();
        foreach (var file in Files) file.PropertyChanged += (_, _) => { Reviewed = false; Result = null; Changed(); };
    }
    public void SetDestination(ShopDestinationInspection inspection)
    { _destinationContents = inspection.Contents; DestinationRevision = inspection.Revision; Changed(); }
    public void ApplyResult(ShopTransferResult result)
    {
        if (result.Success) _destinationContents = WorkflowShopTransfer.DesiredTree(Selection);
        DestinationRevision = result.DestinationRevision; Result = result.Message; Changed();
    }
    partial void OnReviewedChanged(bool value) => Changed();
    partial void OnResultChanged(string? value) => OnPropertyChanged(nameof(Summary));
    private void Changed() { OnPropertyChanged(nameof(RequiresReview)); OnPropertyChanged(nameof(RequiresReplacement)); OnPropertyChanged(nameof(CanCopy)); OnPropertyChanged(nameof(Summary)); }
}

public partial class WorkflowShopTransferViewModel(string root, string sourceId, WorkflowOrder order, WorkflowWorkspace workspace,
    Func<CancellationToken, Task> validateOrder, Func<string, Task> clipboard, Action<ShopTransferResult[]>? transferred = null,
    Func<DateTimeOffset, CancellationToken, Task>? recordHandoff = null) : ObservableObject
{
    private CancellationTokenSource? _operation;
    private DateTimeOffset? _preparedAt;
    private bool _handoffRecorded;
    public ObservableCollection<WorkflowShopProjectChoice> Projects { get; } = [];
    public string Title => "Задание в цех · " + order.DisplayNumber;
    public string Root => root;
    [ObservableProperty] private WorkflowShopProjectChoice? _selectedProject;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _allowReplacement;
    [ObservableProperty] private bool _allPrepared;
    [ObservableProperty] private bool _textCopied;
    [ObservableProperty] private bool _wasCancelled;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    public bool CanEdit => !IsBusy;
    public string CloseCaption => IsBusy ? CanCancel ? "Отменить" : "Подождите…" : "Закрыть";
    public bool CanCloseOrCancel => !IsBusy || CanCancel;
    public bool HasExisting => Projects.Any(p => p.RequiresReplacement);
    public bool ShowReplacement => HasExisting && !AllPrepared;
    public bool CanCopy => !IsBusy && !AllPrepared && Projects.Count > 0 && Projects.All(p => p.CanCopy) && (!HasExisting || AllowReplacement);
    public bool CanStartAutomatically => CanCopy && !Projects.Any(p => p.RequiresReview || p.RequiresReplacement);
    public bool CanCopyText => !IsBusy && AllPrepared;
    public bool NeedsClipboardRetry => AllPrepared && !TextCopied;
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnCanCancelChanged(bool value) => Notify();
    partial void OnAllPreparedChanged(bool value) => Notify();
    partial void OnAllowReplacementChanged(bool value) => Notify();
    partial void OnTextCopiedChanged(bool value) => OnPropertyChanged(nameof(NeedsClipboardRetry));
    private void Notify()
    { foreach (var name in new[] { nameof(CanEdit), nameof(CanCopy), nameof(CanStartAutomatically), nameof(CanCopyText), nameof(HasExisting), nameof(ShowReplacement), nameof(CloseCaption), nameof(CanCloseOrCancel), nameof(NeedsClipboardRetry) }) OnPropertyChanged(name); }
    public void Cancel() => _operation?.Cancel();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true; Error = null; AllPrepared = false; TextCopied = false; WasCancelled = false; AllowReplacement = false; Status = "Подбор файлов…";
        _preparedAt = null; _handoffRecorded = false;
        Projects.Clear(); SelectedProject = null;
        using var operation = new CancellationTokenSource(); _operation = operation; CanCancel = true;
        try
        {
            await validateOrder(operation.Token);
            var plans = await WorkflowShopDiscovery.DiscoverAsync(root, order, workspace, operation.Token);
            var rows = new List<WorkflowShopProjectChoice>();
            foreach (var plan in plans)
            {
                ShopDestinationInspection inspection = new(null, null); string? problem = null;
                try { inspection = await Task.Run(() => WorkflowShopTransfer.InspectDestinationAsync(plan, sourceId, order.Id, operation.Token), operation.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException) { problem = Describe(ex); }
                var row = new WorkflowShopProjectChoice(plan, inspection.Revision, problem); row.SetDestination(inspection); rows.Add(row);
            }
            foreach (var row in rows) { row.PropertyChanged += (_, _) => { AllPrepared = false; TextCopied = false; Notify(); }; Projects.Add(row); }
            SelectedProject = Projects.FirstOrDefault(p => !p.CanCopy || p.RequiresReplacement) ?? Projects.FirstOrDefault();
            Status = Projects.All(p => p.CanCopy) && !HasExisting ? $"Комплект определён: проектов {Projects.Count}." :
                HasExisting ? "Комплект уже передавался и изменился. Проверьте файлы перед заменой." : $"Проектов: {Projects.Count}. Проверьте отмеченные позиции.";
        }
        catch (OperationCanceledException) { WasCancelled = true; Status = "Подбор отменён. Файлы не копировались."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Describe(ex); Status = null; }
        finally { _operation = null; CanCancel = false; IsBusy = false; }
    }

    [RelayCommand]
    public async Task CopyAsync()
    {
        if (!CanCopy) return;
        IsBusy = true; Error = null; TextCopied = false; WasCancelled = false; Status = "Проверка комплектов…";
        _preparedAt = null; _handoffRecorded = false;
        using var operation = new CancellationTokenSource(); _operation = operation; CanCancel = true;
        try
        {
            var results = await WorkflowShopTransfer.CopyAsync(root, sourceId, order.Id, Projects.Select(p => p.Selection).ToArray(), AllowReplacement,
                validateOrder, new Progress<string>(s => { if (IsBusy && ReferenceEquals(_operation, operation)) Status = s; }), operation.Token);
            foreach (var result in results)
            {
                var row = Projects.Single(p => p.Plan.Project.Id == result.ProjectId);
                row.ApplyResult(result);
            }
            if (results.Length > 0) transferred?.Invoke(results);
            var count = results.Count(r => r.Success);
            AllPrepared = count == Projects.Count;
            if (AllPrepared) _preparedAt = DateTimeOffset.UtcNow;
            WasCancelled = operation.IsCancellationRequested;
            Status = $"Подготовлено {count} из {Projects.Count}." + (operation.IsCancellationRequested ? " Операция отменена; готовые комплекты сохранены." : "");
            Error = results.FirstOrDefault(r => !r.Success)?.Message;
            if (AllPrepared && !operation.IsCancellationRequested) await CopyTextCoreAsync(operation.Token);
            else SelectedProject = Projects.FirstOrDefault(p => results.Any(r => r.ProjectId == p.Plan.Project.Id && !r.Success)) ??
                Projects.FirstOrDefault(p => results.All(r => r.ProjectId != p.Plan.Project.Id));
        }
        catch (OperationCanceledException) { WasCancelled = true; Status = "Подготовка отменена. Проверьте готовые папки перед повтором."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Describe(ex); }
        finally { _operation = null; CanCancel = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task CopyTextAsync()
    {
        if (!CanCopyText) return;
        IsBusy = true;
        try { await CopyTextCoreAsync(); } finally { IsBusy = false; }
    }
    private async Task CopyTextCoreAsync(CancellationToken token = default)
    {
        try
        {
            await validateOrder(token); token.ThrowIfCancellationRequested();
            if (!_handoffRecorded && recordHandoff is not null && _preparedAt is { } at)
            {
                try { await recordHandoff(at, token); _handoffRecorded = true; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Error = "Файлы подготовлены, но отметку передачи сохранить не удалось. " +
                        (ex is WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException ? ex.Message : "Проверьте доступ к базе и обновите журнал (F5).");
                    Status = "Файлы подготовлены. Завершение передачи требует проверки."; return;
                }
            }
            token.ThrowIfCancellationRequested();
            CanCancel = false; // The clipboard API cannot cancel a write once it has started.
            await clipboard(WorkflowCuttingTask.Prepare(order, workspace.Employees, workspace.Customers, workspace.Machines).Text);
            TextCopied = true; Error = null; Status = $"Подготовлено {Projects.Count} из {Projects.Count}. Текст задания скопирован.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { WasCancelled = true; Status = "Файлы подготовлены. Копирование текста отменено."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = "Файлы подготовлены, но текст не скопирован. Проверьте актуальность заявки и повторите копирование текста."; }
    }
    private static string Describe(Exception ex) => ex is WorkflowValidationException or WorkflowConflictException ? ex.Message :
        "Не удалось подготовить задание. Проверьте доступ к рабочим папкам и папке цеха, затем обновите подбор.";
}
