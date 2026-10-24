using Jornal.Core.Orders;
using Jornal.Desktop.Customization;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public RowColorViewModel? CreateRowColorForm()
    {
        if (!CanUpdateSelectedOrders) return null;
        var expected = ClipboardSelection();
        var colors = expected.Select(order => order.Color).Distinct().ToArray();
        var initial = colors.Length == 1 && colors[0].HasValue ? colors[0]!.Value : TechnologistColorArgb;
        return new RowColorViewModel(initial, expected.Length, FrequentTechnologistColors.Concat(OtherTechnologistColors), async color =>
        {
            return await UpdateOrdersAsync(expected.Select(order => order.Id).ToArray(), () => orderEditor!.SetColorAsync(expected, color), $"Цвет изменён · заявок: {expected.Length}");
        }, () => ErrorMessage, () => CanUpdateSelectedOrders);
    }
    private EntrySuggestions _entrySuggestions = new();
    public string EntrySuggestionsSummary => $"Менеджеры: {_entrySuggestions.Managers.Length} · заказчики: {_entrySuggestions.Customers.Length} · материалы: {_entrySuggestions.Materials.Length}";
    public EntrySuggestions CaptureEntrySuggestions() => _entrySuggestions.Normalize();
    public void ApplyEntrySuggestions(EntrySuggestions value)
    {
        _entrySuggestions = value.Normalize();
        OnPropertyChanged(nameof(EntrySuggestionsSummary));
    }
}
