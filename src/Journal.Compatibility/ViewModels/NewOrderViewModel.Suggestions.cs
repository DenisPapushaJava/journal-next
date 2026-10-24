using Jornal.Desktop.Customization;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel
{
    internal void SetEntrySuggestions(EntrySuggestions suggestions)
    {
        Manager.Suggestions = suggestions.Managers;
        Customer.Suggestions = suggestions.Customers;
        Material.Suggestions = suggestions.Materials;
        foreach (var item in BatchItems) item.Form.SetEntrySuggestions(suggestions);
    }
}
