using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Core.Results;

namespace Jornal.Desktop.Customization;

internal sealed class ResultRowOrderConverter : JsonConverter<ResultRowOrder>
{
    public override ResultRowOrder Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<ResultRowOrder>(reader.GetString(), out var value) && Enum.IsDefined(value))
            return value;
        reader.Skip(); return ResultRowOrder.ThicknessAndMaterial;
    }
    public override void Write(Utf8JsonWriter writer, ResultRowOrder value, JsonSerializerOptions options) =>
        writer.WriteStringValue((Enum.IsDefined(value) ? value : ResultRowOrder.ThicknessAndMaterial).ToString());
}
