using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public sealed record PriceThicknessChangePreview(string Thickness, string Price, int OrderCount)
{
    public string Orders => $"Заявок с прежней толщиной: {OrderCount.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))}";
}

public sealed partial class PriceEditorViewModel
{
    private PriceTableChange? _pendingThicknessChange;
    private long _draftRevision;
    private Dictionary<PriceRowViewModel, PriceRate> _uncertainTargets = [];
    [ObservableProperty] private bool _isConfirmingThickness;
    [ObservableProperty] private IReadOnlyList<PriceThicknessChangePreview> _thicknessChanges = [];
    public bool HasAffectedOrders => ThicknessChanges.Any(row => row.OrderCount > 0);
    public bool CanConfirmThickness => IsConfirmingThickness && !IsBusy && _pendingThicknessChange is not null;
    partial void OnIsConfirmingThicknessChanged(bool value)
    {
        NotifyAvailability(); OnPropertyChanged(nameof(CanConfirmThickness));
        ConfirmThicknessChangesCommand.NotifyCanExecuteChanged(); KeepOriginalThicknessesCommand.NotifyCanExecuteChanged();
    }
    partial void OnThicknessChangesChanged(IReadOnlyList<PriceThicknessChangePreview> value) => OnPropertyChanged(nameof(HasAffectedOrders));

    private void ValidateThicknessDuplicates()
    {
        var duplicates = Rows.Where(row => row.TryGetThickness(out _)).GroupBy(row => { row.TryGetThickness(out var thickness); return thickness; })
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var row in Rows)
            row.HasThicknessConflict = row.TryGetThickness(out var value) && (duplicates.Contains(value) ||
                row.IsThicknessChanged && Rows.Any(other => !other.IsNew && other != row && other.Original.ThicknessMillimeters == value));
    }

    private PriceTableChange BuildPriceChange()
    {
        if (_snapshot is null) throw new ArgumentException("Обновите прайс.");
        var byName = Rows.Where(row => !row.IsNew).ToDictionary(row => row.Original.ColumnName, StringComparer.Ordinal);
        return new PriceTableChange(_snapshot, _snapshot with
        {
            Rates = _snapshot.Rates.Select(rate => byName[rate.ColumnName].DraftRate())
                .Concat(Rows.Where(row => row.IsNew).Select(row => row.DraftRate())).ToArray()
        }).Snapshot();
    }

    private async Task PrepareThicknessConfirmationAsync(PriceTableChange change, CancellationToken token)
    {
        var impact = editor as IPriceImpactReader ?? reader as IPriceImpactReader ??
            throw new InvalidOperationException("Источник не поддерживает проверку изменения толщин.");
        var revision = _draftRevision;
        var usage = await impact.ReadThicknessUsageAsync(change, token);
        var confirmed = (change with { ConfirmedUsage = usage.ToArray() }).Snapshot();
        if (revision != _draftRevision || !IsEditing || RequiresReload) return;
        _pendingThicknessChange = confirmed;
        string Number(decimal value) => value.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"));
        string Price(int? value) => value?.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) ?? "не задан";
        ThicknessChanges = confirmed.RenamedRates.Zip(usage).Select(pair => new PriceThicknessChangePreview(
            $"{Number(pair.First.Before.ThicknessMillimeters)} → {Number(pair.First.After.ThicknessMillimeters)} мм",
            $"{Price(pair.First.Before.RublesPerMeter)} → {Price(pair.First.After.RublesPerMeter)} ₽/м", pair.Second.OrderCount)).ToArray();
        IsConfirmingThickness = true;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmThickness))]
    private async Task ConfirmThicknessChangesAsync(CancellationToken cancellationToken)
    {
        if (!CanConfirmThickness || _pendingThicknessChange is not { } change) return;
        if (!PriceTableChange.Matches(change.Values, BuildPriceChange().Values)) { DismissThicknessConfirmation(); return; }
        DismissThicknessConfirmation(); IsSaving = true;
        try { await PersistAsync(change, cancellationToken); }
        finally { IsSaving = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmThickness))]
    private void KeepOriginalThicknesses()
    {
        if (!CanConfirmThickness) return;
        var renamed = Rows.Where(row => !row.IsNew && row.IsThicknessChanged).ToArray();
        if (Rows.Count + renamed.Length > 255) { DismissThicknessConfirmation(); Error = "Недостаточно места для новых толщин в прайсе MDB."; return; }
        var copies = renamed.Select(row => new PriceRowViewModel(row.DraftRate(), true)).ToArray();
        foreach (var row in renamed) { row.ThicknessValue = row.Thickness; row.Value = row.Original.RublesPerMeter?.ToString(CultureInfo.InvariantCulture) ?? ""; }
        DismissThicknessConfirmation(); ReplaceRows(Rows.Concat(copies).OrderBy(row => row.Original.ThicknessMillimeters).ToArray());
    }

    public void DismissThicknessConfirmation() { _pendingThicknessChange = null; IsConfirmingThickness = false; ThicknessChanges = []; }

    private PriceRowViewModel[] ReapplyDraft(PriceTable table, PriceRowViewModel[]? draft)
    {
        if (draft is null) return table.Rates.Select(rate => new PriceRowViewModel(rate)).ToArray();
        PriceRate? Resolve(PriceRowViewModel row)
        {
            PriceRate? Find(PriceRate target) => table.Rates.SingleOrDefault(rate => rate.ColumnName == target.ColumnName && rate.ThicknessMillimeters == target.ThicknessMillimeters);
            if (row.IsNew && _uncertainTargets.TryGetValue(row, out var added)) return Find(added);
            if (!row.IsNew && Find(row.Original) is { } unchanged) return unchanged;
            if (_uncertainTargets.TryGetValue(row, out var attempted)) return Find(attempted);
            return row.TryGetThickness(out var thickness) && (row.IsNew || row.IsThicknessChanged)
                ? table.Rates.SingleOrDefault(rate => rate.ThicknessMillimeters == thickness && rate.ColumnName == PriceTableChange.ColumnName(thickness)) : null;
        }
        if (!table.HasRow || _snapshot is null || _snapshot.Rates.Any(old =>
            !table.Rates.Any(rate => rate.ColumnName == old.ColumnName && rate.ThicknessMillimeters == old.ThicknessMillimeters) &&
            !draft.Any(row => row.Original == old && !row.IsNew && Resolve(row) is not null)))
            throw new PriceReadException("Структура прайса изменилась. Ввод сохранён; отмените правки для загрузки новой структуры.");
        var loaded = table.Rates.Select(rate =>
        {
            var previous = draft.FirstOrDefault(row => Resolve(row) == rate);
            var model = new PriceRowViewModel(rate);
            if (previous?.IsChanged == true)
            {
                if (previous.IsNew || previous.IsPriceChanged) model.Value = previous.Value;
                if (previous.IsThicknessChanged) model.ThicknessValue = previous.ThicknessValue;
            }
            return model;
        });
        return loaded.Concat(draft.Where(row => row.IsNew && Resolve(row) is null)).ToArray();
    }
}
