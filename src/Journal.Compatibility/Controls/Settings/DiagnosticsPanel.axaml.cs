using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls.Settings;

public partial class DiagnosticsPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public DiagnosticsPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (IsEffectivelyVisible) (DataContext as DiagnosticsViewModel)?.Refresh(); };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
    private async void OnExport(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not DiagnosticsViewModel model || model.IsExporting) return;
        model.IsExporting = true;
        model.ExportStatus = null;
        try
        {
            var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Сохранить диагностический отчёт", SuggestedFileName = $"Journal-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip", FileTypeChoices = [new FilePickerFileType("Архив ZIP") { Patterns = ["*.zip"] }]
            });
            if (file is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await model.Service.FlushLocalAsync(timeout.Token);
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await Task.Run(() => model.Service.Store.Export(stream));
            model.ExportStatus = "Отчёт сохранён";
        }
        catch (Exception) { model.ExportStatus = "Не удалось сохранить отчёт. Выберите другую папку."; }
        finally { model.IsExporting = false; }
    }
}
