using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Orders;
using Jornal.Desktop.Import;
namespace Jornal.Desktop.ViewModels;
public partial class NewOrderViewModel
{
    [ObservableProperty] private bool _isDraggingFiles;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDropRejection))] private string? _dropRejection;
    public bool HasDropRejection => DropRejection is not null;
    public string? CheckFileDrop(IReadOnlyList<string> paths)
    {
        if (!CanAddFiles) return RequiresReload ? "Обновите список заявок (F5)." : HasPendingImport ? "Завершите замену спецификации." : "Дождитесь завершения операции.";
        if (SpecificationImportService.CheckDropFiles(paths) is { } error) return error;
        var preserved = !IsBatchMode && HasCurrentParameters ? 1 : 0;
        return (IsBatchMode || paths.Count > 1) && BatchItems.Count + paths.Count + preserved > ManualOrderBatch.MaximumCount
            ? $"В одном пакете — до {ManualOrderBatch.MaximumCount} файлов." : null;
    }
}
