using Avalonia.Media;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    internal bool IsEditingShopColor { get; private set; }
    private uint _expectedShopColor;
    private HsvColor _shopPickerColor;
    public HsvColor PickerSpectrumColor
    {
        get => IsEditingShopColor ? _shopPickerColor : ProfileSpectrumColor;
        set
        {
            if (!IsEditingShopColor) { ProfileSpectrumColor = value; return; }
            _shopPickerColor = new HsvColor(1, value.H, value.S, value.V); NotifyColorPicker();
        }
    }
    public string PickerColorHex => "#" + (PickerSpectrumColor.ToRgb().ToUInt32() & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
    public string PickerCaption => IsEditingShopColor ? "Цвет отметки передачи в цех" : "Личный цвет технолога";
    internal void BeginColorPicker(bool shop)
    {
        IsEditingShopColor = shop; _expectedShopColor = Journal.ShopHandoffColorArgb;
        _shopPickerColor = new(Color.FromUInt32(_expectedShopColor)); NotifyColorPicker();
    }
    internal bool CommitShopColor()
    {
        if (!CanEdit) return false;
        Error = Journal.SaveShopHandoffColor(_expectedShopColor, _shopPickerColor.ToRgb().ToUInt32());
        return Error is null;
    }
    private void NotifyColorPicker()
    {
        OnPropertyChanged(nameof(PickerSpectrumColor)); OnPropertyChanged(nameof(PickerColorHex)); OnPropertyChanged(nameof(PickerCaption));
        var color = PickerSpectrumColor.ToRgb().ToUInt32();
        foreach (var option in FrequentColors.Concat(OtherColors)) option.IsSelected = option.Argb == color;
    }
}
