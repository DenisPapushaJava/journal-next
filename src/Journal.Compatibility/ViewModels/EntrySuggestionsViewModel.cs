using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Desktop.Customization;

namespace Jornal.Desktop.ViewModels;

public partial class EntrySuggestionsViewModel : ObservableObject
{
    private readonly Func<EntrySuggestions, string?> _save;
    public EntrySuggestionsViewModel(EntrySuggestions current, Func<EntrySuggestions, string?> save)
    {
        ManagersText = string.Join("\n", current.Managers); CustomersText = string.Join("\n", current.Customers);
        MaterialsText = string.Join("\n", current.Materials); _save = save;
    }
    [ObservableProperty] private string _managersText = "";
    [ObservableProperty] private string _customersText = "";
    [ObservableProperty] private string _materialsText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool HasError => Error is not null;
    public event EventHandler? Saved;
    [RelayCommand]
    private void Save()
    {
        try { var value = EntrySuggestions.FromText(ManagersText, CustomersText, MaterialsText); Error = _save(value); if (Error is null) Saved?.Invoke(this, EventArgs.Empty); }
        catch (ArgumentException exception) { Error = exception.Message; }
    }
}
