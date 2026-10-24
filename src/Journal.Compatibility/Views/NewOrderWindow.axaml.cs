using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class NewOrderWindow : Window
{
    private NewOrderViewModel? _viewModel;

    public NewOrderWindow()
    {
        InitializeComponent();
        ConfigureFileDrop();
        AddHandler(OrderEntryControl.PricesRequestedEvent, OnThicknessPricesRequested);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) { _viewModel.Saved -= OnSaved; _viewModel.StopPricePreview(); }
            _viewModel = DataContext as NewOrderViewModel;
            if (_viewModel is not null) { _viewModel.Saved += OnSaved; if (IsVisible) _viewModel.StartThicknessOptions(); }
        };
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowSizing.FitToWorkingArea(this);
        this.GetVisualDescendants().OfType<OrderEntryControl>()
            .FirstOrDefault(control => control.IsEffectivelyVisible && control.DataContext == _viewModel?.Manager)?.FocusInput();
        var token = _importLifetime.Token;
        _viewModel?.StartThicknessOptions();
        try
        {
            if (_viewModel is not null)
            { await _viewModel.LoadMachinesAsync(token); await _viewModel.RefreshThicknessOptionsAsync(cancellationToken: token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        { if (!token.IsCancellationRequested && _viewModel is not null) _viewModel.SaveError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось загрузить толщины."); }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is { IsEditingPrices: true } || _viewModel?.SaveCommand.IsRunning == true && !_viewModel.IsSaved) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _importLifetime.Cancel();
        _importLifetime.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.CancelImportRead();
            _viewModel.ReplaceImportCommand.Cancel();
            _viewModel.CalculatePriceCommand.Cancel();
            _viewModel.StopThicknessOptions();
            _viewModel.Saved -= OnSaved;
        }
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _viewModel?.SaveCommand.IsRunning != true)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnSaved(object? sender, EventArgs e) => Close(_viewModel?.CreatedOrder);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    private void OnSaveClick(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
        this.GetVisualDescendants().OfType<OrderEntryControl>()
            .FirstOrDefault(field => field.IsEffectivelyVisible && field.IsEffectivelyEnabled && field.DataContext is OrderEntryField { HasError: true })?.FocusInput());
}
