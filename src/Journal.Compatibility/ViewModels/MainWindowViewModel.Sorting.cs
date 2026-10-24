using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public string DefaultSortingSummary => FilterDate is null ? "Порядок: Дата и номер ↓" : "Порядок: Номер ↑";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCustomSort))]
    private string _sortingSummary = "";
    [ObservableProperty] private string _sortingDescription = "";
    public bool HasCustomSort => SortingSummary.Length > 0;
    public event EventHandler? SortingResetRequested;
    [RelayCommand] private void ResetSorting() => SortingResetRequested?.Invoke(this, EventArgs.Empty);
}
