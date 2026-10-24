using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class EntrySuggestionsWindow : Window
{
    private EntrySuggestionsViewModel? Model => DataContext as EntrySuggestionsViewModel;
    public EntrySuggestionsWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); WindowSizing.FitToWorkingArea(this); if (Model is { } model) model.Saved += OnSaved; }
    protected override void OnClosed(EventArgs e) { if (Model is { } model) model.Saved -= OnSaved; base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void OnSaved(object? sender, EventArgs e) => Close();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
