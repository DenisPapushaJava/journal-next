using Jornal.Desktop.Tables;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private double _rowHighlightStrength = RowHighlightSettings.Default;
    public double RowHighlightStrength
    {
        get => _rowHighlightStrength;
        set => SetProperty(ref _rowHighlightStrength, RowHighlightSettings.Normalize(value));
    }
}
