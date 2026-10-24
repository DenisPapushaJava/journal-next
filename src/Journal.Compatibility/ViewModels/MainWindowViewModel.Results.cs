using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly HashSet<int> _resultOrderIds = [];
    private int _selectedCount;
    internal const string ResultCopiedFeedback = "Скопировано";
    internal const string ResultClearedFeedback = "Результат очищен";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanClearResult)), NotifyPropertyChangedFor(nameof(CanCopyResult))]
    private string _resultText = string.Empty;
    private const string DefaultResultMessage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowResultFeedback))] private string _resultMessage = DefaultResultMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowResultFeedback)), NotifyPropertyChangedFor(nameof(ResultFeedbackBrush))]
    private bool _hasResultError;
    public bool ShowResultFeedback => HasResultError || ResultMessage != DefaultResultMessage;
    internal bool HasTransientResultFeedback => !HasResultError &&
        ResultMessage is ResultCopiedFeedback or ResultClearedFeedback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAddResult)), NotifyPropertyChangedFor(nameof(CanClearResult)),
     NotifyPropertyChangedFor(nameof(CanCopyResult)), NotifyPropertyChangedFor(nameof(CanEditResult)), NotifyPropertyChangedFor(nameof(CanConfigureSource)), NotifyPropertyChangedFor(nameof(CanCheckUpdates))]
    private bool _isCopyingResult;
    public bool CanAddResult => CanEditResult && CanUseJournal && _selectedCount > 0;
    public bool CanClearResult => CanEditResult && ResultText.Length > 0;
    public bool CanCopyResult => CanEditResult && !string.IsNullOrWhiteSpace(ResultText);
    public bool CanEditResult => CanChoosePeriod && !IsLoadingOrders && !IsCopyingResult;

    partial void OnResultTextChanged(string value)
    {
        if (value.Length == 0) _resultOrderIds.Clear();
        HasResultError = false;
        ResultMessage = DefaultResultMessage;
    }

    public void AddOrdersToResult(IEnumerable<OrderRowViewModel> selected)
    {
        if (!CanEditResult || !CanUseJournal) return;
        var visible = Orders.ToDictionary(row => row.Id, row => row.Source);
        var orders = selected.Select(row => row.Id).Distinct()
            .Where(visible.ContainsKey).Select(id => visible[id]).ToArray();
        var unpriced = orders.Where(order => order.StoredCuttingPrice is null).ToArray();
        if (unpriced.Length > 0)
        {
            HasResultError = true;
            var numbers = string.Join(", ", unpriced.Take(3).Select(order => order.SpecificationNumber));
            if (unpriced.Length > 3) numbers += $" и ещё {unpriced.Length - 3}";
            ResultMessage = $"Не рассчитана цена: {numbers}. Выберите заявки с рассчитанной ценой.";
            return;
        }
        if (orders.Length == 0)
        {
            HasResultError = false;
            ResultMessage = "Выберите заявки";
            return;
        }
        if (ResultRowOrder == ResultRowOrder.SelectionOrder)
        {
            var selectedOrders = orders.ToDictionary(order => order.Id);
            orders = _selectionOrder.Where(selectedOrders.ContainsKey).Select(id => selectedOrders[id])
                .Concat(orders.Where(order => !_selectedOrderIds.Contains(order.Id))).ToArray();
        }
        var result = OrderResultFormatter.Format(orders, CaptureResultPricing(), ResultRowOrder);
        ResultText = result;
        _resultOrderIds.Clear();
        foreach (var order in orders) _resultOrderIds.Add(order.Id);
        HasResultError = false;
        ResultMessage = $"Сформировано заявок: {orders.Length}";
    }

    [RelayCommand]
    private void ClearResult()
    {
        if (!CanEditResult) return;
        ResultText = string.Empty;
        _resultOrderIds.Clear();
        HasResultError = false;
        ResultMessage = ResultClearedFeedback;
    }

    public async Task CopyResultAsync(IResultClipboard clipboard)
    {
        AppDiagnostics.Current?.Mark(DiagnosticOperation.Clipboard);
        if (!CanCopyResult) return;
        var text = ResultText;
        var style = CaptureResultStyle();
        IsCopyingResult = true;
        HasResultError = false;
        ResultMessage = "Копирование…";
        try
        {
            await clipboard.WriteAsync(ResultClipboardContent.FromText(text, style));
            ResultMessage = ResultCopiedFeedback;
        }
        catch (Exception exception)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Clipboard, exception);
            HasResultError = true;
            ResultMessage = "Не удалось скопировать. Результат сохранён — попробуйте ещё раз.";
        }
        finally { IsCopyingResult = false; }
    }
}
