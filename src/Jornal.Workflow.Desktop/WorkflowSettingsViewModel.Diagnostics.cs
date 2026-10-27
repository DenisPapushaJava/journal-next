using CommunityToolkit.Mvvm.ComponentModel;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    public bool HasDiagnostics => Journal.Diagnostics is not null;
    public string DiagnosticsDirectory => WorkflowIdentity.DiagnosticsDirectory;
    public bool CanExportDiagnostics => CanEdit && HasDiagnostics;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _diagnosticsStatus;
    partial void OnIsExportingChanged(bool value) => NotifyAvailability();

    public async Task ExportDiagnosticsAsync(Func<CancellationToken, Task<Stream?>> openDestination, CancellationToken token = default)
    {
        if (!CanExportDiagnostics || Journal.Diagnostics is not { } diagnostics) return;
        IsExporting = true; DiagnosticsStatus = null;
        try
        {
            await using var stream = await openDestination(token);
            if (stream is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await diagnostics.FlushLocalAsync(timeout.Token);
            if (stream.CanSeek) stream.SetLength(0);
            await Task.Run(() => diagnostics.Store.Export(stream), token);
            DiagnosticsStatus = $"Отчёт программы «{WorkflowIdentity.Name}» сохранён.";
        }
        catch (OperationCanceledException) { DiagnosticsStatus = "Сохранение отчёта отменено."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { DiagnosticsStatus = "Не удалось сохранить отчёт. Выберите другую папку."; }
        finally { IsExporting = false; }
    }
}
