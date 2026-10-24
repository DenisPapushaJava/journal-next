using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

internal sealed record ThicknessContext(string CuttingType, string MaterialId, CuttingMachine? Machine, bool RequiresHansId);

public sealed partial class ThicknessOptionsViewModel : ObservableObject
{
    private readonly OrderEntryField _field;
    private readonly Func<ThicknessContext> _context;
    private readonly Action<Exception?> _showHelp;
    private ThicknessPriceSource _source;
    private CancellationTokenSource? _request;
    private long _generation;
    private IReadOnlyList<decimal>? _available;
    private ThicknessContext? _loadedContext;
    private ThicknessContext? _requestedContext;
    private string? _loadError;
    private bool _stopped;
    private Task _loadingTask = Task.CompletedTask;
    internal Func<Task>? ReloadAll { get; set; }
    internal bool IsActive { get; private set; }
    public bool IsVerified { get; private set; }
    internal ThicknessOptionsViewModel(OrderEntryField field, ThicknessPriceSource source, Func<ThicknessContext> context, Action<Exception?> showHelp)
    {
        _field = field; _source = source; _context = context; _showHelp = showHelp;
        field.ThicknessOptions = this; field.Choices = [];
        field.PropertyChanged += FieldChanged;
    }
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasUnavailableValue;
    [ObservableProperty] private string? _tableName;
    public bool CanSave => !IsLoading && !HasUnavailableValue;
    public bool CanReload => !IsLoading && !_stopped && _source.IsAvailable;
    public bool CanOpenPrices => CanReload && TableName is not null;
    internal event EventHandler? Changed;
    partial void OnIsLoadingChanged(bool value) { _field.IsChoiceLoading = value; Notify(); }
    partial void OnHasUnavailableValueChanged(bool value) => Notify();
    partial void OnTableNameChanged(string? value) => Notify();
    private void Notify()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanReload)); OnPropertyChanged(nameof(CanOpenPrices));
        ReloadCommand.NotifyCanExecuteChanged(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void FieldChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(OrderEntryField.Value)) ValidateValue(); }
    internal void UseSource(ThicknessPriceSource source) => _source = source;
    internal void SourceChanged() { if (IsActive && !_stopped && _context() != _requestedContext) _ = RefreshAsync(); }
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (ReloadAll is not null) await ReloadAll();
        else { _source.Invalidate(); await RefreshAsync(); }
    }
    public Task WaitForLoadAsync(CancellationToken cancellationToken = default) => _loadingTask.WaitAsync(cancellationToken);
    public Task RefreshAsync(CancellationToken cancellationToken = default) => _loadingTask = RefreshCoreAsync(cancellationToken);
    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        IsActive = true;
        var generation = ++_generation;
        CancelRequest();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _request = request;
        var context = _context();
        _requestedContext = context;
        IsVerified = false; _loadError = null;
        if (_loadedContext != context) { _available = null; _field.Choices = []; HasUnavailableValue = false; _field.ChoiceError = null; }
        TableName = null; IsLoading = true;
        try
        {
            if (context.CuttingType.Length == 0 || !_source.IsAvailable) { _field.Choices = []; return; }
            if (context.RequiresHansId && context.MaterialId is not ("-1" or "0" or "1" or "2"))
            { _field.Choices = []; _field.ChoiceError = "Проверьте ID материала HansLaser."; HasUnavailableValue = true; return; }
            var name = CuttingPriceCalculator.SelectTableForMachine(context.CuttingType, context.MaterialId, context.Machine);
            TableName = name;
            var table = await _source.ReadAsync(name, request.Token);
            if (generation != _generation || context != _context()) return;
            _available = CuttingPriceCalculator.AvailableThicknesses(context.CuttingType, context.MaterialId, table, context.Machine);
            _loadedContext = context; IsVerified = true;
            _field.Choices = _available.Select(value => value.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"))).ToArray();
            ValidateValue();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation != _generation || context != _context()) return;
            _loadError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось загрузить толщины. Повторите обновление.");
            _field.ChoiceError = _loadError;
            _showHelp(exception);
        }
        finally
        {
            if (generation == _generation) { _request = null; IsLoading = false; Notify(); }
        }
    }
    private void ValidateValue()
    {
        if (_available is null) return;
        var text = _field.Value.Trim();
        HasUnavailableValue = text.Length > 0 && (!decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) || !_available.Contains(value));
        _field.ChoiceError = _loadError ?? (HasUnavailableValue ? $"{text} мм — нет доступного тарифа для этого станка." : null);
        Notify();
    }
    internal void Stop()
    {
        _stopped = true; ++_generation; CancelRequest(); ReloadCommand.Cancel();
        _field.PropertyChanged -= FieldChanged;
    }
    private void CancelRequest()
    {
        var request = Interlocked.Exchange(ref _request, null);
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    internal void Restart(ThicknessPriceSource source)
    {
        _source = source;
        if (!_stopped) return;
        _stopped = false; _requestedContext = null; IsLoading = false;
        _field.PropertyChanged += FieldChanged; Notify();
    }
}
