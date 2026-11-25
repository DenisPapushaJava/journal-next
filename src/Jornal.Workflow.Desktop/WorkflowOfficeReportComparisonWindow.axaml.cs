using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

internal enum WorkflowOfficeReportChoice { Cancel, Home, Office }

public partial class WorkflowOfficeReportComparisonWindow : Window
{
    private readonly Func<Task<bool>>? _openCopies;

    public WorkflowOfficeReportComparisonWindow() => InitializeComponent();

    internal WorkflowOfficeReportComparisonWindow(string fileName, Func<Task<bool>> openCopies) : this()
    {
        ComparedFile.Text = fileName;
        _openCopies = openCopies;
    }

    internal WorkflowOfficeReportComparisonWindow(WorkflowOfficeFileConflict conflict, Func<Task<bool>> openCopies, bool forShopTransfer = false)
        : this(conflict.Path, openCopies)
    {
        Title = "Версии файла";
        if (conflict.OfficeHash is null)
        {
            ComparisonDescription.Text = "Файл удалён в офисе, а дома изменён. Можно оставить локальную версию или принять удаление. Копия сохранится в папке сравнения.";
            ChooseOfficeVersion.Label = "Принять удаление";
        }
        else
        {
            ComparisonDescription.Text = "Файл изменён и дома, и в офисе. Выберите версию для локальной папки. Обе копии сохранятся. " +
                (forShopTransfer ? "Если выберете домашнюю версию, дальше подтвердите её отправку в офис." :
                    "Домашнюю версию можно отправить позже из меню проекта.");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CancelChoice.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(WorkflowOfficeReportChoice.Cancel); e.Handled = true; }
    }

    private async void OnOpenCopies(object? sender, RoutedEventArgs e)
    {
        try { OpenError.IsVisible = _openCopies is null || !await _openCopies(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { OpenError.IsVisible = true; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(WorkflowOfficeReportChoice.Cancel);
    private void OnOffice(object? sender, RoutedEventArgs e) => Close(WorkflowOfficeReportChoice.Office);
    private void OnHome(object? sender, RoutedEventArgs e) => Close(WorkflowOfficeReportChoice.Home);
}
