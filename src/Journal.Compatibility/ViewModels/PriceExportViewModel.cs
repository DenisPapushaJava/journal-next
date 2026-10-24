using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Infrastructure.Export;

namespace Jornal.Desktop.ViewModels;

public sealed partial class PriceExportChoice(PriceGroup group) : ObservableObject
{
    public PriceGroup Group { get; } = group;
    public string Name => Group.Name;
    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class PriceExportViewModel : ObservableObject
{
    private readonly IPriceReader _reader;
    private CancellationTokenSource? _exportCancellation;
    public PriceExportViewModel(IPriceReader reader, IEnumerable<PriceGroup> groups)
    {
        _reader = reader;
        Items = new(new ObservableCollection<PriceExportChoice>(groups.Select(group => group with { Profiles = group.Profiles.Where(profile => profile.IsUsedInCalculation).ToArray() })
            .Where(group => group.Profiles.Count > 0).Select(group => new PriceExportChoice(group))));
        foreach (var item in Items) item.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(PriceExportChoice.IsSelected)) { Error = null; OnPropertyChanged(nameof(CanExport)); } };
    }
    public ReadOnlyObservableCollection<PriceExportChoice> Items { get; }
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanExport))] private bool _isExporting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool HasError => Error is not null;
    public bool CanExport => !IsExporting && Items.Any(item => item.IsSelected);
    public string SuggestedFileName => $"Прайсы-{DateTime.Now:yyyy-MM-dd}.xlsx";
    public void CancelExport() => _exportCancellation?.Cancel();

    public async Task<bool> ExportAsync(Func<string, Task<string?>> choosePath, CancellationToken cancellationToken = default)
    {
        if (!CanExport) return false;
        var selected = Items.Where(item => item.IsSelected).Select(item => item.Group).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _exportCancellation = cancellation;
        IsExporting = true; Error = null; AccessHostHelp.ShowFor(null);
        try
        {
            var path = await choosePath(SuggestedFileName);
            if (path is null) return false;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            { Error = "Выберите файл с расширением .xlsx."; return false; }
            cancellation.CancelAfter(TimeSpan.FromMinutes(2));
            var workbook = await new PriceExportReader(_reader).ReadAsync(selected, cancellation.Token);
            await PriceWorkbookExporter.SaveAsync(path, workbook, cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!cancellationToken.IsCancellationRequested) Error = "Экспорт отменён. Можно повторить."; return false; }
        catch (PriceExportReadException exception)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception,
                $"Не удалось прочитать прайс «{exception.Machine} — {exception.Profile}». Обновите прайсы и повторите экспорт.");
            AccessHostHelp.ShowFor(exception.InnerException); return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.Unexpected, exception,
                "Не удалось сохранить Excel. Проверьте папку и доступ к файлу."); return false;
        }
        finally { _exportCancellation = null; IsExporting = false; }
    }
}
