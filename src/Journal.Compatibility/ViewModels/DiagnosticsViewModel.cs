using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject
{
    public DiagnosticService Service { get; }
    public DiagnosticsViewModel(DiagnosticService service) { Service = service; _sendAutomatically = service.SendAutomatically; Refresh(); }
    [ObservableProperty] private bool _sendAutomatically;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _exportStatus;
    public string PrivacyText => "Только технические сведения. Данные заявок и документы не отправляются.";
    partial void OnSendAutomaticallyChanged(bool value) { Service.SetAutomatic(value); Refresh(); }
    public void Refresh()
    {
        Error = !Service.PreferencesSaved ? "Выбор не сохранён. Проверьте доступ к папке профиля." :
            !Service.StorageAvailable ? "Не удалось записать диагностику." : null;
        Status = !Service.SendAutomatically ? "Автоотправка отключена" : !Service.Configured ? "Приёмник ещё не подключён" :
            Service.PendingCount > 0 ? $"В очереди: {Service.PendingCount}" :
            Service.LastAcceptedUtc is { } at ? $"Последняя доставка: {at.ToLocalTime():dd.MM HH:mm}" : "Готово к отправке";
    }
}
