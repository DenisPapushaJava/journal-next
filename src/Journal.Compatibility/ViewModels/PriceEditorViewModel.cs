using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public sealed partial class PriceRowViewModel(PriceRate original, bool isNew = false) : ObservableObject
{
    public PriceRate Original { get; } = original;
    public bool IsNew { get; } = isNew;
    public string Thickness => Original.ThicknessMillimeters.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"));
    public string CurrentThickness => IsNew ? "—" : Thickness;
    public string CurrentPrice => IsNew ? "Новая" : Original.RublesPerMeter?.ToString(CultureInfo.GetCultureInfo("ru-RU")) ?? "Не задан";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged)), NotifyPropertyChangedFor(nameof(IsThicknessChanged)),
        NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(HasThicknessError)), NotifyPropertyChangedFor(nameof(ThicknessError))]
    private string _thicknessValue = original.ThicknessMillimeters.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"));
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(HasThicknessError)), NotifyPropertyChangedFor(nameof(ThicknessError))]
    private bool _hasThicknessConflict;
    public bool TryGetThickness(out decimal value) => decimal.TryParse(ThicknessValue?.Trim().Replace(',', '.'),
        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value > 0;
    public bool IsThicknessChanged => !TryGetThickness(out var thickness) || thickness != Original.ThicknessMillimeters;
    public string? ThicknessError => !TryGetThickness(out _) ? "Укажите положительную толщину." :
        HasThicknessConflict ? "Такая толщина уже есть в прайсе." : null;
    public bool HasThicknessError => ThicknessError is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged)), NotifyPropertyChangedFor(nameof(IsPriceChanged)), NotifyPropertyChangedFor(nameof(HasError))]
    private string _value = original.RublesPerMeter?.ToString(CultureInfo.InvariantCulture) ?? "";
    public bool TryGetValue(out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(Value)) return !IsNew;
        if (!int.TryParse(Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed; return true;
    }
    public bool HasError => ThicknessError is not null || HasPriceError;
    public bool HasPriceError => !TryGetValue(out _) && (IsNew || IsPriceChanged);
    public bool IsPriceChanged => TryGetValue(out var value) ? value != Original.RublesPerMeter : Value != Original.RublesPerMeter?.ToString(CultureInfo.InvariantCulture);
    public bool IsChanged => IsNew || IsThicknessChanged || IsPriceChanged;
    public PriceRate DraftRate()
    {
        if (!IsChanged) return Original;
        if (HasError || !TryGetThickness(out var thickness)) throw new ArgumentException("Проверьте толщину и цену.");
        var price = Original.RublesPerMeter;
        if ((IsNew || IsPriceChanged) && !TryGetValue(out price)) throw new ArgumentException("Проверьте цену.");
        return new(thickness, IsNew || IsThicknessChanged ? PriceTableChange.ColumnName(thickness) : Original.ColumnName, price);
    }
}

public enum PriceStorageKind { SharedDatabase, LocalCopy, Session }

public sealed partial class PriceEditorViewModel(IPriceReader reader, IPriceEditor? editor, string source, string sourceDetails,
    PriceStorageKind storage = PriceStorageKind.SharedDatabase) : ObservableObject
{
    private PriceTable? _snapshot;
    public string? InitialTableName { get; set; }
    private PriceGroup _selectedGroup = PriceCatalog.Groups[0];
    private PriceProfile _selectedProfile = PriceCatalog.Groups[0].Profiles[0];
    public ObservableCollection<PriceGroup> Groups { get; } = new(PriceCatalog.Groups);
    public IReadOnlyList<PriceProfile> Profiles => SelectedGroup.Profiles;
    public string Source { get; } = source;
    public string SourceDetails { get; } = sourceDetails;
    public ObservableCollection<PriceRowViewModel> Rows { get; } = [];
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    public PriceGroup SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (value is null || !CanNavigate || !SetProperty(ref _selectedGroup, value)) return;
            _selectedProfile = value.Profiles[0];
            OnPropertyChanged(nameof(Profiles)); OnPropertyChanged(nameof(SelectedProfile));
            ProfileChanged(); RefreshCommand.Execute(null);
        }
    }
    public PriceProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || !CanNavigate || !SetProperty(ref _selectedProfile, value)) return;
            ProfileChanged(); RefreshCommand.Execute(null);
        }
    }
    private void ProfileChanged()
    {
        _uncertainTargets.Clear();
        _snapshot = null; ReplaceRows([]); Error = null; Status = null; RequiresReload = false;
        OnPropertyChanged(nameof(HasProfiles)); OnPropertyChanged(nameof(Usage)); OnPropertyChanged(nameof(TableName)); OnPropertyChanged(nameof(ShowTableName));
        OnPropertyChanged(nameof(ReadOnlyReason)); NotifyAvailability();
    }
    public string TableName => SelectedProfile.TableName;
    public bool ShowTableName => !CuttingMachine.IsCustomTable(TableName);
    public string Usage => SelectedProfile.Usage;
    public bool HasProfiles => Profiles.Count > 1;
    public string ReadOnlyReason => !SelectedProfile.CanEdit ? "Не используется в расчёте · только просмотр" :
        editor is null ? "Только чтение" : "";
    public bool IsReadOnly => !string.IsNullOrEmpty(ReadOnlyReason);
    public string EditScope => storage switch
    {
        PriceStorageKind.Session => "Изменения действуют до закрытия программы",
        PriceStorageKind.LocalCopy => "Изменения сохраняются в локальной копии",
        _ => "Общие тарифы для пользователей этой базы"
    };
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _requiresReload;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _confirmDiscard;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasRows => Rows.Count > 0;
    public bool HasChanges => Rows.Any(row => row.IsChanged) || HasUnstagedRate;
    public int ChangedCount => Rows.Count(row => row.IsChanged);
    public string ChangesText => $"Изменено: {ChangedCount}";
    public bool CanNavigate => !IsLoading && !IsSaving && !IsEditing;
    public bool CanEdit => CanNavigate && !IsReadOnly && !RequiresReload && _snapshot is { HasRow: true, Rates.Count: > 0 };
    public bool CanSave => IsEditing && !IsLoading && !IsSaving && !IsConfirmingThickness && !RequiresReload && !ShowAddRate && HasChanges && Rows.All(row => !row.HasError);
    public bool CanRefresh => !IsLoading && !IsSaving && (!IsEditing || RequiresReload);
    public bool CanCancel => IsEditing && !IsLoading && !IsSaving;
    public bool CanClose => !IsSaving && !IsExportDialogOpen;
    public bool IsBusy => IsLoading || IsSaving;
    public bool CanBrowse => !IsSaving && !IsLoading;
    public bool HasInvalidPrices => IsEditing && Rows.Any(row => row.HasPriceError);
    public string? ThicknessValidationError => IsEditing ? Rows.Select(row => row.ThicknessError).FirstOrDefault(error => error is not null) : null;
    public bool HasInvalidThickness => ThicknessValidationError is not null;
    public event EventHandler? SavingChanged;

    partial void OnErrorChanged(string? value) => AccessHostHelp.ShowFor(null);
    partial void OnIsLoadingChanged(bool value) => NotifyAvailability();
    partial void OnIsSavingChanged(bool value) { NotifyAvailability(); OnPropertyChanged(nameof(CanConfirmThickness)); ConfirmThicknessChangesCommand.NotifyCanExecuteChanged(); KeepOriginalThicknessesCommand.NotifyCanExecuteChanged(); SavingChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnIsEditingChanged(bool value) => NotifyAvailability();
    partial void OnRequiresReloadChanged(bool value) => NotifyAvailability();

    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(CanNavigate), nameof(CanEdit), nameof(CanSave), nameof(CanRefresh), nameof(CanCancel),
            nameof(CanClose), nameof(IsBusy), nameof(CanBrowse), nameof(HasInvalidPrices), nameof(HasRows), nameof(HasChanges), nameof(ChangesText), nameof(IsReadOnly),
            nameof(CanBeginAddRate), nameof(CanStageRate), nameof(ShowExpansionNotice), nameof(CanAddMachine), nameof(CanEditMachineImport), nameof(IsCustomMachine), nameof(CanExport), nameof(ThicknessValidationError), nameof(HasInvalidThickness) }) OnPropertyChanged(name);
        BeginEditCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged(); CancelEditCommand.NotifyCanExecuteChanged();
        BeginAddRateCommand.NotifyCanExecuteChanged(); StageRateCommand.NotifyCanExecuteChanged(); CancelAddRateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Явное обновление после конфликта переносит только изменённые пользователем ячейки.
        var draft = IsEditing ? Rows.ToArray() : null;
        DismissThicknessConfirmation();
        Error = null; Status = null; ConfirmDiscard = false; IsLoading = true;
        try
        {
            if (!IsEditing) await LoadMachineCatalogAsync(cancellationToken);
            if (InitialTableName is { } initial && !IsEditing)
            {
                var group = Groups.FirstOrDefault(value => value.Profiles.Any(profile => profile.TableName == initial)) ??
                    throw new PriceReadException("Прайс станка недоступен. Обновите каталог.");
                InitialTableName = null;
                _selectedGroup = group; _selectedProfile = group.Profiles.First(profile => profile.TableName == initial);
                OnPropertyChanged(nameof(SelectedGroup)); OnPropertyChanged(nameof(Profiles)); OnPropertyChanged(nameof(SelectedProfile));
                ProfileChanged();
            }
            var table = await reader.ReadAsync(TableName, cancellationToken);
            table.Validate();
            if (table.Name != TableName) throw new PriceReadException("Получен прайс другого станка.");
            var loaded = ReapplyDraft(table, draft);
            _snapshot = table with { Rates = table.Rates.ToArray() };
            ReplaceRows(loaded.OrderBy(row => row.Original.ThicknessMillimeters));
            _uncertainTargets.Clear();
            RequiresReload = false;
            Status = !table.HasRow ? "Прайс пуст" : table.Rates.Count == 0 ? "Тарифов нет" :
                draft is not null ? "Прайс обновлён. Проверьте изменения." : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось прочитать прайс.");
            AccessHostHelp.ShowFor(exception);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void BeginEdit() { Error = null; Status = null; IsEditing = true; }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelEdit()
    {
        // После неподтверждённой записи следующий просмотр обязательно перечитывает источник.
        var reload = RequiresReload;
        _uncertainTargets.Clear();
        DismissThicknessConfirmation();
        CloseAddRate();
        if (_snapshot is { } table) ReplaceRows(table.Rates.OrderBy(rate => rate.ThicknessMillimeters).Select(rate => new PriceRowViewModel(rate)));
        IsEditing = false; ConfirmDiscard = false; Error = null; Status = null;
        if (reload) RefreshCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!CanSave || editor is null || _snapshot is null) return;
        Error = null; Status = null; ConfirmDiscard = false; IsSaving = true;
        try
        {
            var change = BuildPriceChange();
            if (change.HasRenames) await PrepareThicknessConfirmationAsync(change, cancellationToken);
            else await PersistAsync(change, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is OrderWriteException write) RequiresReload = write.RequiresReload;
            Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось проверить изменения толщин.");
        }
        finally { IsSaving = false; }
    }

    private async Task PersistAsync(PriceTableChange change, CancellationToken cancellationToken)
    {
        var values = change.Values;
        var committed = false;
        Dictionary<PriceRowViewModel, PriceRate>? attemptedTargets = null;
        try
        {
            attemptedTargets = Rows.ToDictionary(row => row, row => row.DraftRate());
            var saved = await editor!.SaveAsync(change, cancellationToken);
            if (!PriceTableChange.Matches(saved, values))
                throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown, "Тарифы не подтверждены. Обновите прайс перед повтором.");
            committed = true;
            _uncertainTargets.Clear();
            _snapshot = saved;
            ReplaceRows(saved.Rates.OrderBy(rate => rate.ThicknessMillimeters).Select(rate => new PriceRowViewModel(rate)));
            IsEditing = false; RequiresReload = false; Status = "Прайс сохранён";
        }
        catch (OrderWriteException exception)
        {
            RequiresReload = exception.RequiresReload;
            if (exception.Failure == OrderWriteFailure.OutcomeUnknown && attemptedTargets is not null) _uncertainTargets = attemptedTargets;
            Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, "Прайс не сохранён.");
            AccessHostHelp.ShowFor(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (attemptedTargets is not null) _uncertainTargets = attemptedTargets;
            RequiresReload = true; Error = "Сохранение отменено. Обновите прайс перед повтором.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (attemptedTargets is not null) _uncertainTargets = attemptedTargets;
            RequiresReload = true;
            Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, committed
                ? "Прайс сохранён, но экран не обновлён. Обновите прайс; повторная запись не нужна."
                : "Сохранение не подтверждено. Обновите прайс перед повтором.");
            AccessHostHelp.ShowFor(exception);
        }
        finally { IsSaving = false; }
    }

    private void ReplaceRows(IEnumerable<PriceRowViewModel> rows)
    {
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows.Clear();
        foreach (var row in rows) { row.PropertyChanged += RowChanged; Rows.Add(row); }
        ValidateThicknessDuplicates();
        NotifyAvailability();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PriceRowViewModel.Value) or nameof(PriceRowViewModel.ThicknessValue))) return;
        _draftRevision++;
        DismissThicknessConfirmation();
        ValidateThicknessDuplicates(); ConfirmDiscard = false; NotifyAvailability();
    }
}
