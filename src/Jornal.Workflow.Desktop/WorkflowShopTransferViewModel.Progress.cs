using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed class WorkflowShopProgressStep(string caption) : ObservableObject
{
    public string Caption { get; } = caption;
    private bool _current, _done;
    public bool IsCurrent => _current;
    public bool IsDone => _done;
    internal void Set(bool current, bool done)
    {
        SetProperty(ref _current, current, nameof(IsCurrent)); SetProperty(ref _done, done, nameof(IsDone));
    }
}

public partial class WorkflowShopTransferViewModel
{
    [ObservableProperty] private WorkflowShopProgress? _progress;
    [ObservableProperty] private bool _cancellationRequested;
    public WorkflowShopProgressStep[] ProgressSteps { get; } =
        [new("Проверка"), new("Карты"), new("Файлы"), new("Отметка"), new("Текст")];
    public bool IsProgressSuccessful => !IsBusy && AllPrepared && TextCopied && !HasError && !WasCancelled;
    public string ProgressTitle => IsProgressSuccessful ? "Передано в цех" : IsBusy && CancellationRequested ? "Останавливаем подготовку…" :
        HasError ? "Передача требует внимания" : !IsBusy && WasCancelled ? "Подготовка остановлена" : Progress?.Stage switch
    {
        WorkflowShopStage.Signing => "Подписываем карты раскроя",
        WorkflowShopStage.Copying => "Готовим файлы для цеха",
        WorkflowShopStage.Verifying => "Проверяем подготовленные копии",
        WorkflowShopStage.Recording => "Сохраняем отметку передачи",
        WorkflowShopStage.Clipboard => "Копируем текст задания",
        _ => "Проверяем комплект задания"
    };
    public bool HasProgressProject => !string.IsNullOrEmpty(Progress?.Project);
    public bool HasProgressFile => !string.IsNullOrEmpty(Progress?.File);
    public string ProgressProject => Progress?.Project ?? "";
    public string ProgressFile => Progress?.File ?? "";
    public string ProgressActivity => IsProgressSuccessful ? "Можно отправить скопированное сообщение в цех." :
        IsBusy && CancellationRequested ? "Дождитесь безопасной остановки. Готовые комплекты и подписанные карты сохранятся." :
        HasError || !IsBusy && WasCancelled ? Status ?? "Проверьте сообщение ниже." : Progress?.Activity ?? "";
    public bool HasProgressCount => Progress is { Completed: >= 0, Total: > 0 };
    public string ProgressCount => IsProgressSuccessful ? $"Подготовлено проектов: {Projects.Count}" :
        HasProgressCount ? $"Обработано файлов: {Progress!.Completed} из {Progress.Total}" : "";
    partial void OnProgressChanged(WorkflowShopProgress? value)
        => NotifyProgress();
    partial void OnCancellationRequestedChanged(bool value) => NotifyProgress();
    partial void OnStatusChanged(string? value)
    { if (HasError || WasCancelled) OnPropertyChanged(nameof(ProgressActivity)); }
    private void NotifyProgress()
    {
        var index = ProgressStepIndex(Progress?.Stage);
        var success = IsProgressSuccessful;
        for (var i = 0; i < ProgressSteps.Length; i++) ProgressSteps[i].Set(!success && i == index, success || i < index);
        foreach (var name in new[] { nameof(IsProgressSuccessful), nameof(ProgressTitle), nameof(HasProgressProject), nameof(HasProgressFile), nameof(ProgressProject),
            nameof(ProgressFile), nameof(ProgressActivity), nameof(HasProgressCount), nameof(ProgressCount) }) OnPropertyChanged(name);
    }
    private static int ProgressStepIndex(WorkflowShopStage? stage) => stage switch
    {
        WorkflowShopStage.Signing => 1,
        WorkflowShopStage.Copying or WorkflowShopStage.Verifying => 2,
        WorkflowShopStage.Recording => 3, WorkflowShopStage.Clipboard => 4, _ => 0
    };
    private WorkflowShopProgressFeed ObserveProgress(CancellationTokenSource operation) => new(value =>
    {
        if (!IsBusy || CancellationRequested || !ReferenceEquals(_operation, operation) ||
            ProgressStepIndex(value.Stage) < ProgressStepIndex(Progress?.Stage)) return;
        Progress = value;
        Status = ProgressTitle + (HasProgressFile ? " · " + ProgressFile : HasProgressProject ? " · " + ProgressProject : "") +
            (HasProgressCount ? " · " + ProgressCount : "");
    });
}
