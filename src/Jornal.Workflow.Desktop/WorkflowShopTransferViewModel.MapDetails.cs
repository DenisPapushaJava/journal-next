using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowShopTransferViewModel
{
    private MapSigningResult[] _mapSigningResults = [];
    private string _mapSigningSummary = "";
    [ObservableProperty] private bool _mapDetailsExpanded;
    [ObservableProperty] private IReadOnlyList<MapSigningResult> _mapDetails = [];
    public bool HasMapDetails => !IsBusy && _mapSigningResults.Length > 0;
    public string MapSigningSummary => _mapSigningSummary;
    public string MapDetailsCaption => MapDetailsExpanded ? "Скрыть сведения о картах" : "Подробнее о картах";

    // Keep the returned array/strings themselves. No second projection, parsing or file access.
    internal void RememberMapSigningResults(MapSigningResult[] results)
    {
        _mapSigningResults = results;
        var signed = 0; var unchanged = 0; var problems = 0;
        foreach (var result in results)
        {
            if (result.Status == MapSigningStatus.Signed) signed++;
            else if (result.Status == MapSigningStatus.Unchanged) unchanged++;
            else problems++;
        }
        _mapSigningSummary = results.Length == 0 ? "" : $"Карты: обновлено файлов {signed}, без изменений {unchanged}" +
            (problems > 0 ? $", требуют проверки {problems}." : ".");
        if (MapDetailsExpanded) MapDetails = results;
        OnPropertyChanged(nameof(MapSigningSummary)); OnPropertyChanged(nameof(HasMapDetails));
    }
    private void ResetMapSigningDetails()
    {
        MapDetailsExpanded = false; MapDetails = [];
        _mapSigningResults = []; _mapSigningSummary = "";
        OnPropertyChanged(nameof(MapSigningSummary)); OnPropertyChanged(nameof(HasMapDetails));
    }
    partial void OnMapDetailsExpandedChanged(bool value)
    {
        if (value)
        {
            if (!HasMapDetails) { MapDetailsExpanded = false; return; }
            if (!ReferenceEquals(MapDetails, _mapSigningResults)) MapDetails = _mapSigningResults;
        }
        OnPropertyChanged(nameof(MapDetailsCaption));
    }
}
