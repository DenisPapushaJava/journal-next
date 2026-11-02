using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowCalculationHistoryWindow : Window
{
    private bool _opening;
    public WorkflowCalculationHistoryWindow() => InitializeComponent();
    private async void OnOpenReport(object? sender, RoutedEventArgs e)
    {
        if (_opening || DataContext is not WorkflowCalculationHistoryViewModel { Selected.Report: { } report } model) return;
        _opening = true; model.Error = null;
        try
        {
            if (!WorkflowReportFiles.IsReport(report.ArchivePath)) throw new InvalidDataException();
            await Task.Run(() => WorkflowReportFiles.VerifyArchiveAsync(report));
            if (IsVisible && !await LaunchReportAsync(report.ArchivePath)) model.Error = "Не удалось открыть сохранённую спецификацию. Проверьте приложение для HTML/PDF.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible) model.Error = "Сохранённая спецификация недоступна или повреждена. Текущий расчёт не изменён."; }
        finally { _opening = false; }
    }
    protected virtual Task<bool> LaunchReportAsync(string path) => Launcher.LaunchFileInfoAsync(new FileInfo(path));
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
