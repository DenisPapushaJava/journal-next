using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class DatabaseSettingsWindow : Window
{
    private DatabaseSettingsViewModel? _model;
    public DatabaseSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.Connected -= OnConnected;
            _model = DataContext as DatabaseSettingsViewModel;
            if (_model is not null) _model.Connected += OnConnected;
        };
    }
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); Page.FocusInitial(); }
    protected override void OnClosed(EventArgs e)
    {
        Page.CancelPending();
        if (_model is not null) _model.Connected -= OnConnected;
        base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is TextBox && _model?.CanChangeSource == true)
        { _model.ConnectCommand.Execute(null); e.Handled = true; }
    }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConnected(object? sender, EventArgs e) => Close(true);
}
