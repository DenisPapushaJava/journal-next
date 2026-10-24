using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class OrderConfirmationWindow : Window
{
    private OrderConfirmationViewModel? _model;
    public OrderConfirmationWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.Confirmed -= OnConfirmed;
            _model = DataContext as OrderConfirmationViewModel;
            if (_model is not null) _model.Confirmed += OnConfirmed;
        };
    }
    private void OnConfirmed(object? sender, EventArgs e) => Close(true);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (_model is { IsConfirmed: false } && _model.ConfirmCommand.IsRunning) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    { if (_model is not null) _model.Confirmed -= OnConfirmed; base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape && _model?.ConfirmCommand.IsRunning != true) { Close(false); e.Handled = true; } }
}
