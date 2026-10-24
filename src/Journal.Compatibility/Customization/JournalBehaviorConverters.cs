using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Infrastructure.Trash;
using Jornal.Desktop.Tables;

namespace Jornal.Desktop.Customization;

internal sealed class DefaultTrueBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip(); return true;
    }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

internal sealed class TrashRetentionDaysConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var days) && OrderTrashService.IsSupportedRetention(days)) return days;
        reader.Skip(); return OrderTrashService.RetentionDays;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(OrderTrashService.IsSupportedRetention(value) ? value : OrderTrashService.RetentionDays);
}

internal sealed class RowContrastPreferenceConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip(); return false;
    }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

internal sealed class RowHighlightStrengthConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) && double.IsFinite(value))
            return RowHighlightSettings.Normalize(value);
        reader.Skip(); return null;
    }
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } strength) writer.WriteNumberValue(RowHighlightSettings.Normalize(strength));
        else writer.WriteNullValue();
    }
}
