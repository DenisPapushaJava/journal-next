using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowShopFileChoice(ShopFile file) : ObservableObject
{
    public ShopFile File { get; private set; } = file;
    internal void ApplySignedFile(ShopFile value)
    { File = value; OnPropertyChanged(nameof(File)); OnPropertyChanged(nameof(Detail)); }
    public string Name => File.Name;
    public string Detail => (File.Kind == ShopFileKind.Program ? "УП · " : "Карта · ") + File.Reason;
    public bool CanSelect => File.IsReadable;
    [ObservableProperty] private bool _isSelected = file.Suggested;
}

public partial class WorkflowShopProjectChoice : ObservableObject
{
    public ShopProjectFiles Plan { get; private set; }
    public string Name => Plan.Project.Name;
    public string Destination => Plan.Destination;
    public WorkflowShopFileChoice[] Files { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDestination))] private string? _destinationRevision;
    public bool HasDestination => DestinationRevision is not null;
    private ShopDestinationInspection? _destinationInspection;
    private readonly ShopMapBundle _mapBundle;
    private readonly ShopProjectFiles _originalPlan;
    private readonly string? _signingCaption;
    private string? _inputKey;
    public bool RequiresReplacement => _destinationInspection is { Contents: { } contents } target &&
        !WorkflowShopTransfer.MatchesDestination(Selection, contents, target.BundleKey, target.PdfExportVersion, target.InputKey);
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
    [ObservableProperty] private bool _canChooseArchive;
    [ObservableProperty] private string? _archivedFolder;
    public string ArchiveAction => ArchivedFolder is null ? "Указать папку из архива…" : "Выбрать другую папку из архива…";
    partial void OnArchivedFolderChanged(string? value) => OnPropertyChanged(nameof(ArchiveAction));
    public bool RequiresReview => Plan.NeedsReview || Files.Any(f => f.IsSelected != f.File.Suggested);
    public bool CanCopy => Problem is null && (!RequiresReview || Reviewed) && Files.Any(f => f.IsSelected && f.File.Kind == ShopFileKind.Program) &&
        Files.Any(f => f.IsSelected && f.File.Kind == ShopFileKind.Map) && !Files.Any(f => f.IsSelected && (!f.File.IsReadable || f.File.Length == 0));
    public string Summary => Result ?? Problem ?? (CanCopy ? "Комплект выбран" : "Проверьте УП и карты");
    public ShopTransferSelection Selection
    {
        get
        {
            var selected = Files.Where(f => f.IsSelected).Select(f => f.File).ToArray();
            var selection = new ShopTransferSelection(Plan, selected, Reviewed, DestinationRevision)
                { MapBundle = _mapBundle, ArchivedFolder = ArchivedFolder };
            if (_signingCaption is null) return selection;
            _inputKey ??= WorkflowShopTransfer.InputKey(selection with
            {
                Project = _originalPlan,
                Files = _originalPlan.Files.Where((_, index) => Files[index].IsSelected).ToArray()
            }, _signingCaption);
            return selection with { InputKey = _inputKey };
        }
    }
    public WorkflowShopProjectChoice(ShopProjectFiles plan, string? revision, string? problem, ShopMapBundle mapBundle = ShopMapBundle.Separate,
        string? signingCaption = null)
    {
        Plan = plan; DestinationRevision = revision; Problem = problem ?? plan.Problem;
        _originalPlan = plan; _signingCaption = signingCaption;
        _mapBundle = plan.Machine?.ImportProfile == "hans-pdf/v1" ? mapBundle : ShopMapBundle.Separate;
        Files = plan.Files.Select(f => new WorkflowShopFileChoice(f)).ToArray();
        foreach (var file in Files) file.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(WorkflowShopFileChoice.IsSelected)) { _inputKey = null; Reviewed = false; Result = null; Changed(); } };
    }
    public void SetDestination(ShopDestinationInspection inspection)
    { _destinationInspection = inspection; DestinationRevision = inspection.Revision; Changed(); }
    public void ApplyResult(ShopTransferResult result)
    {
        if (result.Success)
        {
            _destinationInspection = new(result.DestinationRevision, WorkflowShopTransfer.DesiredTree(Selection))
                { BundleKey = result.BundleKey, PdfExportVersion = result.PdfExportVersion, InputKey = Selection.InputKey };
        }
        DestinationRevision = result.DestinationRevision; Result = result.Message; Changed();
    }
    internal void ApplySignedFiles(IReadOnlyDictionary<string, ShopFile> files)
    {
        Plan = Plan with { Files = Plan.Files.Select(f => files.GetValueOrDefault(f.SourcePath) ?? f).ToArray() };
        foreach (var choice in Files)
            if (files.TryGetValue(choice.File.SourcePath, out var signed)) choice.ApplySignedFile(signed);
        OnPropertyChanged(nameof(Plan)); Changed();
    }
    partial void OnReviewedChanged(bool value) => Changed();
    partial void OnResultChanged(string? value) => OnPropertyChanged(nameof(Summary));
    private void Changed() { OnPropertyChanged(nameof(RequiresReview)); OnPropertyChanged(nameof(RequiresReplacement)); OnPropertyChanged(nameof(CanCopy)); OnPropertyChanged(nameof(Summary)); }
}

public partial class WorkflowShopTransferViewModel(string root, string sourceId, WorkflowOrder order, WorkflowWorkspace workspace,
    Func<CancellationToken, Task> validateOrder, Func<string, Task> clipboard, Action<ShopTransferResult[]>? transferred = null,
    Func<DateTimeOffset, CancellationToken, Task>? recordHandoff = null, IWorkflowWordMapEditor? wordMapEditor = null,
    Func<Guid[], DateTimeOffset, CancellationToken, Task>? recordProjects = null, ShopMapBundle mapBundle = ShopMapBundle.Separate) : ObservableObject
{
    private CancellationTokenSource? _operation;
    private DateTimeOffset? _preparedAt;
    private bool _handoffRecorded;
    private bool _requiresFileRefresh;
    private bool _noticeDismissed;
    private bool _discarded;
    private WorkflowOfficeShopSnapshot? _officeSources;
    internal Func<WorkflowOfficeShopFile[]?, IProgress<WorkflowShopProgress>, CancellationToken, Task<WorkflowOfficeShopSnapshot>>? PrepareOfficeSources { get; init; }
    internal Func<CancellationToken, Task<IWorkflowShopPublication>>? OpenPublication { get; init; }
    internal bool ReviewOnly { get; set; }
    private WorkflowOfficeShopReview? _officeReview;
    internal WorkflowOfficeShopReview? OfficeReview
    {
        get => _officeReview;
        private set
        {
            if (!SetProperty(ref _officeReview, value)) return;
            OnPropertyChanged(nameof(OfficeReviewCaption));
            NotifyOfficeReviewActions();
        }
    }
    public string OfficeReviewCaption => OfficeReview?.Conflict is null ? "Отправить файлы и продолжить" : "Сравнить версии…";
    public bool CanActOnOfficeReview => !IsBusy && OfficeReview is not null && PrepareOfficeSources is not null;
    public bool CanShowShopDetails => !IsBusy && !CanActOnOfficeReview;
    private void NotifyOfficeReviewActions()
    {
        OnPropertyChanged(nameof(CanActOnOfficeReview));
        OnPropertyChanged(nameof(CanShowShopDetails));
    }
    public bool ShowNotice => !_noticeDismissed || IsBusy || HasError;
    public bool CanDismissNotice => !IsBusy && AllPrepared && TextCopied && Error is null;
    internal void DismissSuccessNotice()
    { if (CanDismissNotice) { _noticeDismissed = true; OnPropertyChanged(nameof(ShowNotice)); } }
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
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    partial void OnErrorChanged(string? value)
    { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowNotice)); NotifyProgress(); }
    public bool CanEdit => !IsBusy;
    public string CloseCaption => IsBusy ? CanCancel ? "Отменить" : "Подождите…" : "Закрыть";
    public bool CanCloseOrCancel => !IsBusy || CanCancel;
    public bool HasExisting => Projects.Any(p => p.RequiresReplacement);
    public bool ShowReplacement => HasExisting && !AllPrepared;
    public bool CanCopy => !IsBusy && !_requiresFileRefresh && !AllPrepared && Projects.Count > 0 && Projects.All(p => p.CanCopy) && (!HasExisting || AllowReplacement);
    public bool CanStartAutomatically => CanCopy && !Projects.Any(p => p.RequiresReview || p.RequiresReplacement);
    public bool CanCopyText => !IsBusy && AllPrepared;
    public bool NeedsClipboardRetry => AllPrepared && !TextCopied;
    partial void OnIsBusyChanged(bool value) { Notify(); NotifyProgress(); NotifyOfficeReviewActions(); OnPropertyChanged(nameof(HasMapDetails)); }
    partial void OnCanCancelChanged(bool value) => Notify();
    partial void OnAllPreparedChanged(bool value) => Notify();
    partial void OnAllowReplacementChanged(bool value) => Notify();
    partial void OnTextCopiedChanged(bool value) => OnPropertyChanged(nameof(NeedsClipboardRetry));
    private void Notify()
    { foreach (var name in new[] { nameof(CanEdit), nameof(CanCopy), nameof(CanStartAutomatically), nameof(CanCopyText), nameof(HasExisting), nameof(ShowReplacement), nameof(CloseCaption), nameof(CanCloseOrCancel), nameof(NeedsClipboardRetry), nameof(CanDismissNotice), nameof(ShowNotice) }) OnPropertyChanged(name); }
    public void Cancel()
    {
        if (_operation is null || !CanCancel || CancellationRequested) return;
        CancellationRequested = true; CanCancel = false; Status = "Останавливаем подготовку…"; _operation.Cancel();
    }

    internal void Discard()
    {
        _discarded = true;
        Cancel();
        if (!IsBusy) _ = ReleaseOfficeSourcesAsync();
    }

    internal void ChooseArchivedFolder(WorkflowShopProjectChoice row, string path)
    {
        if (_discarded || IsBusy || !row.CanChooseArchive || !Projects.Contains(row)) return;
        row.ArchivedFolder = path;
        Error = null;
        Status = "При повторной подготовке проверим архивный комплект и создадим задание по прежнему пути.";
    }

    private Task ReleaseOfficeSourcesAsync()
    {
        var sources = _officeSources;
        _officeSources = null;
        return sources?.DeleteAsync() ?? Task.CompletedTask;
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    internal Task ContinueOfficeReviewAsync(WorkflowOfficeShopReview request) =>
        ReferenceEquals(request, OfficeReview) ? LoadAsync(request.Conflict is null ? request.Files : null) : Task.CompletedTask;

    private async Task LoadAsync(WorkflowOfficeShopFile[]? approved = null)
    {
        if (IsBusy || _discarded) return;
        _noticeDismissed = false; OnPropertyChanged(nameof(ShowNotice));
        IsBusy = true; Error = null; AllPrepared = false; TextCopied = false; WasCancelled = false; AllowReplacement = false; Status = "Подбор файлов…";
        var retry = OfficeReview;
        OfficeCalculationProjectId = null;
        OfficeReview = null;
        _preparedAt = null; _handoffRecorded = false;
        _requiresFileRefresh = false;
        ResetMapSigningDetails();
        Projects.Clear(); SelectedProject = null;
        using var operation = new CancellationTokenSource(); _operation = operation; CanCancel = true;
        CancellationRequested = false; Progress = new(WorkflowShopStage.Discovery, Activity: "Ищем УП и карты рядом с проектами");
        using var progress = ObserveProgress(operation);
        try
        {
            await ReleaseOfficeSourcesAsync();
            await validateOrder(operation.Token);
            if (PrepareOfficeSources is not null)
            {
                if (approved is not null) Status = "Отправляем изменения в офис…";
                _officeSources = await PrepareOfficeSources(approved, progress, operation.Token);
            }
            var plans = await WorkflowShopDiscovery.DiscoverAsync(root, _officeSources?.Order ?? order, workspace, operation.Token, wordMapEditor, progress);
            var rows = new List<WorkflowShopProjectChoice>();
            foreach (var discovered in plans)
            {
                var plan = discovered;
                if (_officeSources?.UnavailableFiles.GetValueOrDefault(plan.Project.Id) is { } unavailable)
                {
                    var names = string.Join(", ", unavailable.Take(3));
                    var more = unavailable.Length > 3 ? $" и ещё {unavailable.Length - 3}" : "";
                    plan = plan with { ReviewReason = $"В офисе недоступны файлы: {names}{more}. Проверьте комплект или закройте файлы и обновите подбор." +
                        (plan.ReviewReason is { } reason ? "\n" + reason : "") };
                }
                progress.Report(new(WorkflowShopStage.Checking, plan.Project.Name, Activity: "Проверяем папку задания в цехе"));
                ShopDestinationInspection inspection = new(null, null); string? problem = null;
                try { inspection = await Task.Run(() => WorkflowShopTransfer.InspectDestinationAsync(plan, sourceId, order.Id, operation.Token, progress), operation.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                {
                    problem = Describe(ex);
                    if (_officeSources is not null)
                        problem += " Если папка ещё синхронизируется, дождитесь её обновления и повторите подбор.";
                }
                if (_officeSources is not null && inspection.Revision is null && (plan.Project.ShopHandoff is not null ||
                    order.HasCurrentShopHandoff && order.Projects.All(p => p.ShopHandoff is null)))
                    plan = plan with { ReviewReason = "Проект уже передавался, а по этому пути папка пока не видна. Проверьте путь и синхронизацию перед подтверждением." +
                        (plan.ReviewReason is { } reason ? "\n" + reason : "") };
                string? caption = null;
                if (_officeSources is { } sources && problem is null && plan.Problem is null)
                    caption = WorkflowProjectComment.Format(sources.Order, plan.Project, workspace.Employees, workspace.Customers);
                var row = new WorkflowShopProjectChoice(plan, inspection.Revision, problem, mapBundle, caption); row.SetDestination(inspection); rows.Add(row);
            }
            foreach (var row in rows) { row.PropertyChanged += (_, _) => { AllPrepared = false; TextCopied = false; Notify(); }; Projects.Add(row); }
            SelectedProject = Projects.FirstOrDefault(p => !p.CanCopy || p.RequiresReplacement) ?? Projects.FirstOrDefault();
            Status = Projects.All(p => p.CanCopy) && !HasExisting ? $"Комплект определён: проектов {Projects.Count}." :
                HasExisting ? "Комплект уже передавался и изменился. Проверьте файлы перед заменой." : $"Проектов: {Projects.Count}. Проверьте отмеченные позиции.";
        }
        catch (WorkflowOfficeShopReviewRequiredException ex)
        {
            OfficeReview = ex.Review;
            Error = ex.Message;
            Status = "Проверьте файлы перед отправкой в цех.";
        }
        catch (WorkflowOfficeShopCalculationRequiredException ex)
        {
            OfficeCalculationProjectId = ex.ProjectId;
            Error = ex.Message;
            Status = "Обновите расчёт перед передачей в цех.";
        }
        catch (OperationCanceledException)
        {
            if (approved is not null) OfficeReview = retry;
            WasCancelled = true;
            Status = approved is null ? "Подбор отменён. Файлы не копировались." :
                "Отправка остановлена. Уже переданные в офис файлы сохранены.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (approved is not null) OfficeReview = retry;
            Error = Describe(ex);
            Status = approved is null ? null : "Проверьте отправку файлов в офис.";
        }
        finally
        {
            if (_discarded || WasCancelled || Error is not null) await ReleaseOfficeSourcesAsync();
            _operation = null; CanCancel = false; IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task CopyAsync()
    {
        if (!CanCopy || _discarded) return;
        IsBusy = true; Error = null; TextCopied = false; WasCancelled = false; Status = "Проверка комплектов…";
        _preparedAt = null; _handoffRecorded = false;
        ResetMapSigningDetails();
        using var operation = new CancellationTokenSource(); _operation = operation; CanCancel = true;
        CancellationRequested = false; Progress = new(WorkflowShopStage.Checking, Activity: "Проверяем выбранные карты и программы");
        using var details = ObserveProgress(operation);
        try
        {
            using var publication = OpenPublication is null ? null : await OpenPublication(operation.Token);
            var selections = Projects.Select(p => p.Selection).ToArray();
            if (publication is not null)
                for (var index = 0; index < selections.Length; index++)
                {
                    var selection = selections[index];
                    var revision = selection.ArchivedFolder is null ? selection.DestinationRevision :
                        await Task.Run(() => WorkflowShopTransfer.PublicationRevisionAsync(selection, sourceId, order.Id, operation.Token), operation.Token);
                    try
                    {
                        await publication.UpdateAsync(new(ShopPublicationStep.Check, selection.Project.Project.Id,
                            WorkflowShopTransfer.PublicationPath(root, selection.Project.Destination), revision), operation.Token);
                    }
                    catch (WorkflowValidationException ex) when (selection.DestinationRevision is null && selection.ArchivedFolder is null)
                    {
                        var row = Projects[index];
                        row.CanChooseArchive = true;
                        SelectedProject = row;
                        throw new WorkflowValidationException(ex.Message + " Если папку перенесли в архив, укажите её в подробностях проекта.");
                    }
                }
            var results = await WorkflowShopTransfer.PreflightAsync(root, sourceId, order.Id, selections, AllowReplacement, validateOrder, token: operation.Token, word: wordMapEditor, details: details);
            if (results.Length == 0)
            {
                // Signing mutates working maps, never the source selection or unrelated documents.
                // Until every selected map is verified, there must be no destination writes.
                _requiresFileRefresh = true;
                Progress = new(WorkflowShopStage.Signing, Activity: "Подписываем карты раскроя");
                Status = "Подписываем карты…";
                // Away from the office only disposable copies are edited. Recheck
                // the journal and office bytes before publishing the signed files.
                Func<CancellationToken, Task> validateSigning = _officeSources is null ? validateOrder :
                    static token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
                var signing = await WorkflowMapSigning.SignSelectedAsync(_officeSources?.Order ?? order, workspace, selections,
                    validateSigning, operation.Token, wordMapEditor, details: details);
                RememberMapSigningResults(signing);
                var problem = signing.FirstOrDefault(r => r.NeedsAttention || r.VerifiedFile is null);
                if (problem is not null || signing.Length != selections.Sum(s => s.Files.Count(f => f.Kind == ShopFileKind.Map)))
                {
                    Error = problem?.Details ?? "Не все выбранные карты подтверждены. Обновите подбор и проверьте карты.";
                    Status = _officeSources is null ? "Передача остановлена до копирования. Уже подписанные карты сохранены." :
                        "Передача остановлена до копирования. Рабочие карты остались в офисной папке.";
                    SelectedProject = Projects.FirstOrDefault(p => p.Plan.Project.Id == problem?.ProjectId) ?? SelectedProject;
                    return;
                }
                var signedFiles = signing.ToDictionary(r => r.VerifiedFile!.SourcePath, r => r.VerifiedFile!, StringComparer.OrdinalIgnoreCase);
                foreach (var project in Projects) project.ApplySignedFiles(signedFiles);
                selections = Projects.Select(p => p.Selection).ToArray();
                // Preflight already required confirmation for all pre-existing content changes.
                // Only our verified signature updates may now replace an otherwise identical receipt.
                var replaceSignature = signing.Any(r => r.Status == MapSigningStatus.Signed);
                Progress = new(WorkflowShopStage.Copying, Activity: "Готовим файлы для цеха");
                Status = "Копируем файлы…";
                results = await WorkflowShopTransfer.CopyAsync(root, sourceId, order.Id, selections, AllowReplacement || replaceSignature,
                    validateOrder, token: operation.Token, word: wordMapEditor, details: details,
                    validateProjectSource: _officeSources?.ValidateProject, publication: publication);
                _requiresFileRefresh = false;
            }
            // Stop file callbacks before publishing an outcome or awaiting partial handoff recording.
            details.Dispose();
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
            if (!AllPrepared && count > 0 && !operation.IsCancellationRequested && recordProjects is not null)
            {
                try
                {
                    var ready = results.Where(r => r.Success).Select(r => r.ProjectId).ToHashSet();
                    await RecordVerifiedProjectsAsync(Projects.Where(p => ready.Contains(p.Plan.Project.Id)), publication,
                        ct => recordProjects(ready.ToArray(), DateTimeOffset.UtcNow, ct), operation.Token);
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { Error = (Error is null ? "" : Error + " ") + "Не удалось подтвердить отметки готовых проектов. " + Describe(ex); }
            }
            if (AllPrepared && !operation.IsCancellationRequested)
                await CopyTextCoreAsync(publication, operation.Token);
            else SelectedProject = Projects.FirstOrDefault(p => results.Any(r => r.ProjectId == p.Plan.Project.Id && !r.Success)) ??
                Projects.FirstOrDefault(p => results.All(r => r.ProjectId != p.Plan.Project.Id));
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            Status = _officeSources is null ? "Подготовка отменена. Подписанные карты сохранены; обновите подбор перед повтором." :
                "Подготовка отменена. Обновите подбор перед повтором.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Describe(ex); Status = "Подготовка остановлена. Проверьте сообщение ниже."; }
        finally
        {
            if (_discarded) await ReleaseOfficeSourcesAsync();
            _operation = null; CanCancel = false; IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyTextAsync()
    {
        if (!CanCopyText || _discarded) return;
        _noticeDismissed = false; OnPropertyChanged(nameof(ShowNotice));
        IsBusy = true; TextCopied = false; Error = null; WasCancelled = false; CancellationRequested = false;
        using var operation = new CancellationTokenSource(); _operation = operation; CanCancel = true;
        Status = _handoffRecorded ? "Копирование текста…" : "Проверяем готовые файлы…";
        try
        {
            using var publication = OpenPublication is null || _handoffRecorded ? null : await OpenPublication(operation.Token);
            await CopyTextCoreAsync(publication, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { CancelFinalization(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = Describe(ex); Status = "Файлы подготовлены. Завершение передачи требует проверки."; }
        finally
        {
            if (_discarded) await ReleaseOfficeSourcesAsync();
            _operation = null; CanCancel = false; IsBusy = false;
        }
    }

    private async Task RecordVerifiedProjectsAsync(IEnumerable<WorkflowShopProjectChoice> projects, IWorkflowShopPublication? publication,
        Func<CancellationToken, Task> record, CancellationToken token)
    {
        var selections = projects.Select(project => project.Selection).ToArray();
        var proofs = new List<WorkflowShopTransfer.DestinationVerification>(selections.Length);
        try
        {
            await Task.Run(async () =>
            {
                if (_officeSources is { } sources)
                    foreach (var selection in selections)
                        await sources.ValidateProject(selection, token);
                foreach (var selection in selections)
                    proofs.Add(await WorkflowShopTransfer.VerifyDestinationForRecordingAsync(selection, sourceId, order.Id, token));
                if (publication is not null)
                    foreach (var selection in selections)
                        await publication.UpdateAsync(new(ShopPublicationStep.Confirm, selection.Project.Project.Id,
                            WorkflowShopTransfer.PublicationPath(root, selection.Project.Destination), selection.DestinationRevision), token);
                foreach (var proof in proofs)
                    await proof.RecheckAsync(token);
            }, token);
            token.ThrowIfCancellationRequested();
            CanCancel = false;
            await record(token);
        }
        finally { foreach (var proof in proofs) proof.Dispose(); }
    }

    private async Task CopyTextCoreAsync(IWorkflowShopPublication? publication, CancellationToken token = default)
    {
        try
        {
            await validateOrder(token); token.ThrowIfCancellationRequested();
            if (!_handoffRecorded && recordHandoff is not null && _preparedAt is { } at)
            {
                Progress = new(WorkflowShopStage.Recording, Activity: "Сохраняем отметку «В работе»");
                Status = "Сохраняем отметку передачи…";
                try
                {
                    await RecordVerifiedProjectsAsync(Projects, publication, ct => recordHandoff(at, ct), token);
                    _handoffRecorded = true;
                }
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
            Progress = new(WorkflowShopStage.Clipboard, Activity: "Помещаем сообщение для цеха в буфер обмена");
            Status = "Копируем текст задания…";
            await clipboard(WorkflowCuttingTask.Prepare(order, workspace.Employees, workspace.Customers, workspace.Machines).Text);
            TextCopied = true; Error = null;
            Status = PrepareOfficeSources is null ? $"Передано в цех: {Projects.Count} из {Projects.Count}. Текст задания скопирован." :
                $"В папке Яндекс Диска: {Projects.Count} из {Projects.Count}. Отметка сохранена, текст скопирован.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { CancelFinalization(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = "Файлы подготовлены, но текст не скопирован. Проверьте актуальность заявки и повторите копирование текста.";
          Status = "Файлы подготовлены. Текст задания не скопирован."; }
    }
    private void CancelFinalization()
    { WasCancelled = true; Status = "Файлы подготовлены. Завершение передачи отменено."; }
    internal Guid? OfficeCalculationProjectId { get; private set; }

    private static string Describe(Exception ex) => ex is WorkflowValidationException or WorkflowConflictException or WorkflowOfficeSyncUnavailableException ? ex.Message :
        "Не удалось подготовить задание. Проверьте доступ к рабочим папкам и папке цеха, затем обновите подбор.";
}
