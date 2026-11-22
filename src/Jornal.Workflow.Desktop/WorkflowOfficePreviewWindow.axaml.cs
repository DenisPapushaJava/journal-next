using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfficePreviewWindow : Window
{
    private readonly WorkflowOfficePreviewClient _client;
    private CancellationTokenSource? _pending;

    public WorkflowOfficePreviewWindow() : this(new WorkflowOfficePreviewClient()) { }

    internal WorkflowOfficePreviewWindow(WorkflowOfficePreviewClient client)
    {
        _client = client;
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OfficePreviewAddress.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pending?.Cancel();
        OfficePreviewKey.Clear();
        base.OnClosed(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        if (_pending is not null) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        SetBusy(true);
        ShowStatus("Читаем заявки с сервера…");
        OfficePreviewOrders.ItemsSource = null;
        OfficePreviewOrders.IsVisible = false;
        try
        {
            var page = await _client.ReadAsync(OfficePreviewAddress.Text, OfficePreviewKey.Text, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            OfficePreviewOrders.ItemsSource = page.Orders;
            OfficePreviewOrders.IsVisible = page.Orders.Length > 0;
            ShowStatus(page.TotalCount == 0 ? "Активных заявок пока нет." :
                $"Показано {page.Orders.Length} из {page.TotalCount} активных заявок.");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (OperationCanceledException) { ShowStatus("Сервер долго не отвечает. Проверьте соединение и повторите попытку.", error: true); }
        catch (WorkflowValidationException ex) { ShowStatus(ex.Message, error: true); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        { ShowStatus("Не удалось прочитать список заявок. Проверьте версию сервера и повторите попытку.", error: true); }
        finally
        {
            _pending = null;
            if (!pending.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ConnectionFields.IsEnabled = RunOfficePreview.IsEnabled = !busy;
        OfficePreviewProgress.IsVisible = busy;
    }

    private void ShowStatus(string text, bool error = false)
    {
        OfficePreviewStatus.Text = text;
        OfficePreviewStatus.Classes.Set("office-preview-error", error);
    }
}
