using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel? Model => DataContext as SettingsViewModel;
    public bool CustomizeRequested { get; private set; }
    public bool CanRestart => Model?.CanRestart == true && OwnedWindows.Count == 0;
    public SettingsWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowSizing.FitToWorkingArea(this);
        if (Model is { } model)
        {
            model.Journal.PropertyChanged += OnJournalChanged;
            model.PropertyChanged += OnSettingsChanged;
            UpdateTheme(); AcknowledgeDiagnostics();
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) (SettingsNavigation.ContainerFromIndex(Model?.SelectedIndex ?? 0) ?? SettingsNavigation).Focus();
        }, DispatcherPriority.Input);
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SettingsViewModel.SelectedIndex)) AcknowledgeDiagnostics(); }
    private void AcknowledgeDiagnostics()
    {
        if (Model is { IsDiagnostics: true, Diagnostics: { } diagnostics })
        { diagnostics.Service.AcknowledgePreviousFatal(); diagnostics.Refresh(); }
    }
    private void OnJournalChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme)) UpdateTheme(); }
    private void UpdateTheme() => RequestedThemeVariant = Model?.Journal.IsDarkTheme == true ? ThemeVariant.Dark : ThemeVariant.Light;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model?.CanClose == false) e.Cancel = true;
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        Model?.Journal.CommitResultMinimumPrice();
        Model?.Journal.CommitTechnologistColorCode();
        DatabasePage.CancelPending();
        if (Model is { } model) { model.Journal.PropertyChanged -= OnJournalChanged; model.PropertyChanged -= OnSettingsChanged; model.Dispose(); }
        base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is TextBox && Model is { IsDatabase: true, Database: { } database } && database.CanChangeSource && database.ConnectCommand.CanExecute(null))
        { database.ConnectCommand.Execute(null); e.Handled = true; }
    }
    private void OnMinimumPriceLostFocus(object? sender, RoutedEventArgs e) => Model?.Journal.CommitResultMinimumPrice();
    private async void OnEntrySuggestionsClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanNavigate: true } model || Owner is not MainWindow main || OwnedWindows.Count > 0) return;
        var form = new EntrySuggestionsViewModel(model.Journal.CaptureEntrySuggestions(), main.SaveEntrySuggestions);
        await new EntrySuggestionsWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
    }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnCustomizeClick(object? sender, RoutedEventArgs e)
    {
        if (Model?.CanCustomize != true) return;
        CustomizeRequested = true; Close();
    }
    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri("https://github.com/DenisPapushaJava/journal-v2.0")); }
        catch (Exception) { if (Model is { } model) model.LinkError = "Не удалось открыть браузер."; }
    }
}
