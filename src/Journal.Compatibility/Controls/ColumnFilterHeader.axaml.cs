using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

public partial class ColumnFilterHeader : UserControl
{
    private OrderColumnFilterViewModel? _model;
    private Flyout FilterPopup => (Flyout)ColumnFilterButton.Flyout!;
    public bool IsFilterOpen => FilterPopup.IsOpen;
    public void ShowFilter() { if (_model?.CanUseJournal == true) FilterPopup.ShowAt(ColumnFilterButton); }
    public ColumnFilterHeader()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Connect();
        AttachedToVisualTree += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => Disconnect();
    }
    private void Connect()
    {
        var model = DataContext as OrderColumnFilterViewModel;
        if (_model == model) return;
        Disconnect(); _model = model;
        PopupRoot.DataContext = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }
    private void Disconnect()
    {
        FilterPopup.Hide();
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = null;
        PopupRoot.DataContext = null;
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrderColumnFilterViewModel.HasFilter) or nameof(OrderColumnFilterViewModel.ContextVersion)
            || e.PropertyName == nameof(OrderColumnFilterViewModel.CanUseJournal) && _model?.CanUseJournal != true)
            FilterPopup.Hide();
    }
    private void OnFilterOpened(object? sender, EventArgs e)
    {
        if (_model?.CanUseJournal != true) { FilterPopup.Hide(); return; }
        _model.BeginEdit();
        FilterSearch.Focus();
    }
    private void OnFilterClosed(object? sender, EventArgs e) => _model?.BeginEdit();
    private void OnApplyFilter(object? sender, RoutedEventArgs e)
    { if (_model?.ApplyEdit() == true) FilterPopup.Hide(); }
    private void OnClearFilter(object? sender, RoutedEventArgs e)
    {
        if (_model?.CanUseJournal != true) return;
        _model.ClearCommand.Execute(null); FilterPopup.Hide();
    }
}
