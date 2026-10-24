using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Core.Orders;

namespace Jornal.Desktop.Customization;

public sealed record NewOrderColorSettings
{
    public const int DefaultColor = -25089;
    public bool Enabled { get; init; }
    public int ColorArgb { get; init; } = DefaultColor;
    public int EffectiveColor => Enabled && IsAllowed(ColorArgb) ? ColorArgb : OrderColors.None;
    public NewOrderColorSettings Normalize() => IsAllowed(ColorArgb) ? this : new();
    public static bool IsAllowed(int argb) => unchecked((uint)argb) >> 24 == 255 &&
        argb is not (OrderColors.None or OrderColors.Work or OrderColors.Ready or OrderColors.Shipped);
}

internal sealed class NewOrderColorSettingsConverter : JsonConverter<NewOrderColorSettings>
{
    public override bool HandleNull => true;
    public override NewOrderColorSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(nameof(NewOrderColorSettings.ColorArgb), out var color) ||
            color.ValueKind != JsonValueKind.Number || !color.TryGetInt32(out var argb) || !NewOrderColorSettings.IsAllowed(argb)) return new();
        return new() { Enabled = root.TryGetProperty(nameof(NewOrderColorSettings.Enabled), out var enabled) && enabled.ValueKind == JsonValueKind.True, ColorArgb = argb };
    }
    public override void Write(Utf8JsonWriter writer, NewOrderColorSettings value, JsonSerializerOptions options)
    {
        var normalized = (value ?? new()).Normalize();
        writer.WriteStartObject(); writer.WriteBoolean(nameof(NewOrderColorSettings.Enabled), normalized.Enabled);
        writer.WriteNumber(nameof(NewOrderColorSettings.ColorArgb), normalized.ColorArgb); writer.WriteEndObject();
    }
}
