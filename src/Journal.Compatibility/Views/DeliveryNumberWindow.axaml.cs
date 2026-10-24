using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class DeliveryNumberWindow : Window
{
    private DeliveryNumberViewModel? _model;
    public DeliveryNumberWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.Saved -= OnSaved;
            _model = DataContext as DeliveryNumberViewModel;
            if (_model is not null) _model.Saved += OnSaved;
        };
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        DeliveryNumberInput.Focus();
        DeliveryNumberInput.SelectAll();
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_model is { IsSaved: false } && _model.SaveCommand.IsRunning) e.Cancel = true;
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (_model is not null) _model.Saved -= OnSaved;
        base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _model?.SaveCommand.IsRunning != true)
        { Close(false); e.Handled = true; }
    }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnSaved(object? sender, EventArgs e) => Close(true);
}
