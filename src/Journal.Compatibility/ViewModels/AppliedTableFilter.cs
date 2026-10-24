using System.Windows.Input;

namespace Jornal.Desktop.ViewModels;

public sealed record AppliedTableFilter(object Source, string Summary, string Description,
    string ClearLabel, ICommand ClearCommand);
