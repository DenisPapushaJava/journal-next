using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public bool IsApplyingReadSnapshot { get; private set; }
    public event EventHandler? ReadSnapshotApplying;
    public event EventHandler? ReadSnapshotApplied;
    private string? _readSelectionNotice;

    private int ApplyReadSnapshot(IReadOnlyList<OrderSummary> rows, DateOnly? date, bool preserve)
    {
        // Проверяем снимок до замены любых данных на экране.
        var current = rows.ToDictionary(row => row.Id);
        var resultChanged = _allOrders.Any(old => _resultOrderIds.Contains(old.Id) &&
            (date is null || old.OrderDate == date) && (!current.TryGetValue(old.Id, out var value) || value != old));
        var visible = rows.Where(MatchesCurrentFilters).ToDictionary(row => row.Id);
        var viewChanged = Orders.Count != visible.Count || Orders.Any(row =>
            !visible.TryGetValue(row.Id, out var value) || row.Source != value);
        // Выбор фиксируется сейчас, а не перед сетевым запросом.
        var selected = _selectedOrderIds.ToHashSet();
        var removed = preserve ? selected.Count(id => !visible.ContainsKey(id)) : 0;
        if (preserve && viewChanged)
        {
            IsApplyingReadSnapshot = true;
            try
            {
                ReadSnapshotApplying?.Invoke(this, EventArgs.Empty);
                _allOrders = rows; _loadedDate = date; _ordersLoaded = true;
                ApplyLoadedOrders(preserveSelection: true);
                ReadSnapshotApplied?.Invoke(this, EventArgs.Empty);
            }
            catch { _scopeLoadFailed = true; throw; }
            finally { IsApplyingReadSnapshot = false; }
        }
        else
        {
            _allOrders = rows; _loadedDate = date; _ordersLoaded = true;
            if (!preserve) ApplyLoadedOrders();
            else { CountText = $"Заявок: {Orders.Count} из {_allOrders.Count}"; NotifyFilterContextChanged(); }
        }
        if (resultChanged && ResultText.Length > 0)
        {
            HasResultError = true;
            ResultMessage = "Заявки из результата изменились. Проверьте подготовленный текст.";
        }
        return removed;
    }

    private void SetReadSuccessMessage(int removed)
    {
        _readSelectionNotice = removed > 0 ? $" · Из выбора исключено: {removed}" : null;
        UpdatedText = $"Обновлено в {DateTime.Now:HH:mm} · F5 — обновить" + _readSelectionNotice;
    }

    private void ClearReadSelectionNotice()
    {
        if (_readSelectionNotice is not { } notice) return;
        _readSelectionNotice = null;
        if (UpdatedText.EndsWith(notice, StringComparison.Ordinal)) UpdatedText = UpdatedText[..^notice.Length];
    }
}
