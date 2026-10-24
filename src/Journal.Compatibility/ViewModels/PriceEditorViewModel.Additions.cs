using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

public sealed partial class PriceEditorViewModel
{
    [ObservableProperty] private bool _showAddRate;
    [ObservableProperty] private string _newThickness = "";
    [ObservableProperty] private string _newRate = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAdditionError))] private string? _additionError;
    [ObservableProperty] private PriceRowViewModel? _selectedRate;
    public bool HasAdditionError => !string.IsNullOrEmpty(AdditionError);
    private bool HasUnstagedRate => ShowAddRate && (!string.IsNullOrWhiteSpace(NewThickness) || !string.IsNullOrWhiteSpace(NewRate));
    public bool CanBeginAddRate => IsEditing && !IsBusy && !RequiresReload && !ShowAddRate && Rows.Count < 255;
    public bool CanStageRate => IsEditing && ShowAddRate && !IsBusy && !RequiresReload;
    public bool ShowExpansionNotice => IsEditing && storage == PriceStorageKind.SharedDatabase && (ShowAddRate || Rows.Any(row => row.IsNew || row.IsThicknessChanged));

    partial void OnShowAddRateChanged(bool value) => NotifyAvailability();
    partial void OnNewThicknessChanged(string value) { AdditionError = null; ConfirmDiscard = false; NotifyAvailability(); }
    partial void OnNewRateChanged(string value) { AdditionError = null; ConfirmDiscard = false; NotifyAvailability(); }

    [RelayCommand(CanExecute = nameof(CanBeginAddRate))]
    private void BeginAddRate()
    {
        if (!CanBeginAddRate) return;
        NewThickness = ""; NewRate = ""; AdditionError = null; ShowAddRate = true;
    }

    [RelayCommand(CanExecute = nameof(CanStageRate))]
    private void StageRate()
    {
        if (!CanStageRate) return;
        if (!decimal.TryParse(NewThickness?.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var thickness) || thickness <= 0)
        { AdditionError = "Укажите положительную толщину."; return; }
        if (Rows.Any(row => row.TryGetThickness(out var value) && value == thickness || !row.IsNew && row.Original.ThicknessMillimeters == thickness))
        { AdditionError = "Такая толщина уже есть в прайсе."; return; }
        if (!int.TryParse(NewRate?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        { AdditionError = "Укажите целую цену от 0 до 2 147 483 647 ₽."; return; }
        var added = new PriceRowViewModel(new(thickness, PriceTableChange.ColumnName(thickness), amount), isNew: true);
        var sorted = Rows.Append(added).OrderBy(row => row.Original.ThicknessMillimeters).ToArray();
        ReplaceRows(sorted); SelectedRate = added; CloseAddRate();
    }

    [RelayCommand(CanExecute = nameof(CanStageRate))]
    private void CancelAddRate() => CloseAddRate();

    private void CloseAddRate() { ShowAddRate = false; NewThickness = ""; NewRate = ""; AdditionError = null; }

    public void RemoveNewRate(PriceRowViewModel row)
    {
        if (!IsEditing || IsBusy || !row.IsNew || !Rows.Contains(row)) return;
        ReplaceRows(Rows.Where(value => value != row).ToArray());
        if (SelectedRate == row) SelectedRate = null;
    }
}
