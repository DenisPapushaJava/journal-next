using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;
using Jornal.Desktop.Controls;
using Jornal.Desktop.Tables;
using System.ComponentModel;

namespace Jornal.Desktop.Views;

public partial class TrashWindow : Window
{
    private TrashViewModel? Model => DataContext as TrashViewModel;
    private readonly TableContextMenu _contextMenu;
    private TrashViewModel? _connected;
    public TrashWindow()
    {
        InitializeComponent();
        _contextMenu = new(TrashGrid, () => Model is { IsBusy: false } && OwnedWindows.Count == 0, PopulateContextMenu,
            () => { if (Model is { } model) model.Selected = TrashGrid.SelectedItem as TrashItem; });
        DataContextChanged += (_, _) =>
        {
            _contextMenu.Close();
            if (_connected is not null) _connected.PropertyChanged -= OnModelChanged;
            _connected = Model;
            if (_connected is not null) _connected.PropertyChanged += OnModelChanged;
        };
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => _contextMenu.Close();
    private void PopulateContextMenu(TableMenuTarget target, ContextMenu menu)
    {
        if (target.IsHeader || target.Item is not TrashItem item || Model is not { } model) return;
        var version = _contextMenu.Version;
        bool Valid() => _contextMenu.Version == version && menu.IsOpen && ReferenceEquals(model, Model) && model.CanAct && model.Selected == item;
        menu.Items.Add(JournalMenu.Heading("№ " + item.Specification));
        menu.Items.Add(JournalMenu.Item("TrashMenuRestore", "Восстановить…", model.CanAct, OnRestoreClick, Valid, _contextMenu.Close, "rotate-ccw"));
        menu.Items.Add(new Separator());
        menu.Items.Add(JournalMenu.Item("TrashMenuPurge", "Удалить окончательно…", model.CanAct, OnPurgeClick, Valid, _contextMenu.Close, "trash", danger: true));
    }
    protected override async void OnOpened(EventArgs e) { base.OnOpened(e); if (Model is { } model) await model.RefreshCommand.ExecuteAsync(null); }
    protected override void OnClosing(WindowClosingEventArgs e) { if (Model?.IsWriting == true) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    {
        Model?.RefreshCommand.Cancel(); _contextMenu.Dispose();
        if (_connected is not null) _connected.PropertyChanged -= OnModelChanged;
        base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape && Model?.IsWriting != true) { Close(); e.Handled = true; } }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private async void OnRestoreClick(object? sender, RoutedEventArgs e) => await ConfirmAsync(Model?.CreateRestoreConfirmation());
    private async void OnPurgeClick(object? sender, RoutedEventArgs e) => await ConfirmAsync(Model?.CreatePurgeConfirmation());
    private async void OnClearClick(object? sender, RoutedEventArgs e) => await ConfirmAsync(Model?.CreateClearConfirmation());
    private async Task ConfirmAsync(OrderConfirmationViewModel? model)
    {
        if (model is null) return;
        var dialog = new OrderConfirmationWindow { DataContext = model, RequestedThemeVariant = ActualThemeVariant };
        var confirmed = await dialog.ShowDialog<bool>(this);
        if ((confirmed || model.HasOutcome) && Model is { } trash) await trash.RefreshCommand.ExecuteAsync(null);
    }
}
