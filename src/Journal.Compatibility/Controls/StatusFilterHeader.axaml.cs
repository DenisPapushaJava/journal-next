using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

public partial class StatusFilterHeader : UserControl
{
    private MainWindowViewModel? _model;
    private Flyout StatusPopup => (Flyout)StatusFilterButton.Flyout!;
    public bool IsFilterOpen => StatusPopup.IsOpen;
    public void ShowFilter() { if (_model?.CanBrowseJournal == true) StatusPopup.ShowAt(StatusFilterButton); }
    public StatusFilterHeader()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Connect();
        AttachedToVisualTree += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => Disconnect();
    }
    private void Connect()
    {
        var model = DataContext as MainWindowViewModel;
        if (_model == model) return;
        Disconnect(); _model = model;
        PopupRoot.DataContext = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }
    private void Disconnect()
    {
        StatusPopup.Hide();
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = null;
        PopupRoot.DataContext = null;
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.StatusFilter) or nameof(MainWindowViewModel.UseTechnologistColor)
            or nameof(MainWindowViewModel.TechnologistColorArgb) or nameof(MainWindowViewModel.FilterContextVersion)
            || e.PropertyName == nameof(MainWindowViewModel.CanBrowseJournal) && _model?.CanBrowseJournal != true)
            StatusPopup.Hide();
    }
    private void OnFilterOpened(object? sender, EventArgs e)
    {
        if (_model?.CanBrowseJournal != true) { StatusPopup.Hide(); return; }
        _model.BeginStatusFilterEdit();
        AllStatusesCheckBox.Focus();
    }
    private void OnFilterClosed(object? sender, EventArgs e)
    {
        // Сбрасываем не применённый черновик. Escape/клик снаружи не меняют набор строк.
        _model?.BeginStatusFilterEdit();
    }
    private void OnApplyFilter(object? sender, RoutedEventArgs e)
    { if (_model?.ApplyStatusFilterEdit() == true) StatusPopup.Hide(); }
    private void OnClearFilter(object? sender, RoutedEventArgs e)
    {
        if (_model?.CanBrowseJournal != true) return;
        _model.ClearStatusFilterCommand.Execute(null); StatusPopup.Hide();
    }
}
