using Avalonia.Media;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public uint ShopHandoffColorArgb => _localPreferences.ShopHandoffColorArgb;
    public IBrush ShopHandoffBrush => new SolidColorBrush(Color.FromUInt32(ShopHandoffColorArgb));
    private void NotifyShopHandoffColor()
    {
        OnPropertyChanged(nameof(ShopHandoffColorArgb)); OnPropertyChanged(nameof(ShopHandoffBrush));
        foreach (var row in Orders) row.ApplyShopHandoffColor(ShopHandoffColorArgb);
    }
    internal string? SaveShopHandoffColor(uint expected, uint color)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            var saved = _preferences.Update(current =>
            {
                if (current.ShopHandoffColorArgb != expected && current.ShopHandoffColorArgb != color)
                    throw new InvalidOperationException("Цвет изменён в другом окне. Закройте выбор цвета и откройте его заново.");
                return current.ShopHandoffColorArgb == color ? current : current with { ShopHandoffColorArgb = color };
            });
            _localPreferences = _localPreferences with { ShopHandoffColorArgb = saved.ShopHandoffColorArgb };
            NotifyShopHandoffColor(); return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        { return ex is InvalidOperationException ? ex.Message : "Не удалось сохранить цвет на этом компьютере. Проверьте доступ к локальным настройкам."; }
    }
}
