using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record LegacyPricePreviewRow(string Machine, string MaterialGroup, string Thickness, string MeterPrice);

public partial class WorkflowDatabaseViewModel : ObservableObject
{
    private readonly ILegacyPriceCatalogReader _reader;
    private readonly Func<string, LegacyPriceCatalog, CancellationToken, Task<string?>> _apply;
    private LegacyPriceCatalog? _preview;
    private string? _checkedPath;
    public ObservableCollection<LegacyPricePreviewRow> Prices { get; } = [];
    [ObservableProperty] private string _databasePath;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isApplying;
    public bool CanEdit => !IsBusy;
    private bool CanRead => !IsBusy;
    private bool CanImport => !IsBusy && _preview is not null && _checkedPath == DatabasePath;

    public WorkflowDatabaseViewModel(string? path, string status, ILegacyPriceCatalogReader reader,
        Func<string, LegacyPriceCatalog, CancellationToken, Task<string?>> apply)
    { _databasePath = path ?? ""; _status = status; _reader = reader; _apply = apply; }

    partial void OnDatabasePathChanged(string value)
    {
        _preview = null; _checkedPath = null; Prices.Clear(); Error = null;
        Status = "Проверьте выбранную базу перед загрузкой цен."; ImportCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); ReadCommand.NotifyCanExecuteChanged(); ImportCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync(CancellationToken token)
    {
        IsBusy = true; Error = null; _preview = null; _checkedPath = null; Prices.Clear();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var path = LegacyPriceCatalogReader.NormalizePath(DatabasePath);
            var catalog = await _reader.ReadAsync(path, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            DatabasePath = path; _checkedPath = path; _preview = catalog;
            foreach (var rate in catalog.Rates.OrderBy(r => r.Key.Machine).ThenBy(r => r.Key.MaterialGroup).ThenBy(r => r.Key.ThicknessMm))
                Prices.Add(new(rate.Key.Machine, rate.Key.MaterialGroup, SpecificationDraft.N(rate.Key.ThicknessMm), SpecificationDraft.Money(rate.PricePerMeter)));
            Status = $"Станков: {catalog.Machines.Length}. Тарифов: {catalog.Rates.Length}. " +
                "Нажмите «Загрузить цены», чтобы применить их в новом журнале." +
                (catalog.Warnings.Length == 0 ? "" : "\n" + string.Join("\n", catalog.Warnings.Take(6)));
        }
        catch (OperationCanceledException) { Status = token.IsCancellationRequested ? "Проверка отменена." : "База не ответила вовремя. Повторите проверку."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = Describe(ex); Status = "Цены нового журнала не изменены."; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(CancellationToken token)
    {
        if (_preview is not { } catalog || _checkedPath is not { } path) return;
        IsBusy = true; IsApplying = true; Error = null;
        try
        {
            Error = await _apply(path, catalog, token);
            if (Error is null)
            {
                Status = $"Загружено тарифов: {catalog.Rates.Length}. Источник сохранён. Новая пробивка — 1 ₽, изменить можно в «Станках и ценах».";
                _preview = null;
            }
        }
        catch (OperationCanceledException) { Status = "Загрузка отменена."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Describe(ex); }
        finally { IsApplying = false; IsBusy = false; }
    }

    private static string Describe(Exception ex) => ex is WorkflowValidationException ? ex.Message :
        AppDiagnostics.Describe(DiagnosticOperation.PriceRead, ex, "Не удалось загрузить цены. Проверьте доступность выбранной базы.");
}
