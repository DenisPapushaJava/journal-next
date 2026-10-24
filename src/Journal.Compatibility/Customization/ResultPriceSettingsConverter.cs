using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Core.Results;

namespace Jornal.Desktop.Customization;

internal sealed class ResultPriceSettingsConverter : JsonConverter<ResultPriceSettings>
{
    public override bool HandleNull => true;
    public override ResultPriceSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new();
        var minimum = ResultPriceSettings.DefaultMinimum;
        if (root.TryGetProperty(nameof(ResultPriceSettings.MinimumPrice), out var price) &&
            price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var amount)) minimum = amount;
        var useMinimum = !root.TryGetProperty(nameof(ResultPriceSettings.UseMinimumPrice), out var enabled) || enabled.ValueKind != JsonValueKind.False;
        return new ResultPriceSettings { UseMinimumPrice = useMinimum, MinimumPrice = minimum }.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, ResultPriceSettings value, JsonSerializerOptions options)
    {
        var normalized = (value ?? new()).Normalize();
        writer.WriteStartObject();
        writer.WriteBoolean(nameof(ResultPriceSettings.UseMinimumPrice), normalized.UseMinimumPrice);
        writer.WriteNumber(nameof(ResultPriceSettings.MinimumPrice), normalized.MinimumPrice);
        writer.WriteEndObject();
    }
}
