using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

public partial class OrderEntryControl : UserControl
{
    public static readonly RoutedEvent<RoutedEventArgs> PricesRequestedEvent =
        RoutedEvent.Register<OrderEntryControl, RoutedEventArgs>(nameof(PricesRequested), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> PricesRequested
    { add => AddHandler(PricesRequestedEvent, value); remove => RemoveHandler(PricesRequestedEvent, value); }
    public OrderEntryControl() => InitializeComponent();
    private void OnPricesClick(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(PricesRequestedEvent, this));

    public void FocusInput()
    {
        this.BringIntoView();
        if (DataContext is OrderEntryField { HasChoices: true }) Choice.Focus();
        else if (DataContext is OrderEntryField { HasEditableSuggestions: true }) EditableSuggestionsInput.Focus();
        else if (DataContext is OrderEntryField { HasSuggestions: true }) SuggestionsInput.Focus();
        else Input.Focus();
    }
}
