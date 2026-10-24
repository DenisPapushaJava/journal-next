using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using Jornal.Desktop.Updates;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public AppUpdatesViewModel Updates { get; } = appUpdates ?? new(new VelopackAppUpdater());
    public bool CanCheckUpdates => CanChoosePeriod && !IsLoadingOrders && !IsRefreshingOrders && !IsCopyingResult;
    public UpdateRestartDraft CaptureUpdateDraft() => new(1, _source?.Path, ResultText, _source?.Path is null ? [] : _resultOrderIds.ToArray());
    public void RestoreUpdateDraft(IUpdateRestartStore store)
    {
        try
        {
            if (store.Take() is not { } draft) return;
            ResultText = draft.ResultText;
            if (_source?.Path is not null && string.Equals(_source.Path, draft.DatabasePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                _resultOrderIds.UnionWith(draft.ResultOrderIds);
            ResultMessage = "Результат восстановлен после подготовки обновления";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Update, exception);
            ResultMessage = "Не удалось восстановить результат после обновления. Черновик оставлен в профиле пользователя.";
            HasResultError = true;
        }
    }
}
