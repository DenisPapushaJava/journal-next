using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jornal.Desktop.Customization;

public sealed record ResultButtonAppearance
{
    public const int MinimumSize = 32, DefaultSize = 40, MaximumSize = 50;
    public const string DefaultResultColor = "#D6E1EE", DefaultCopyColor = "#35689B";
    public int Size { get; init; } = DefaultSize;
    public string ResultColor { get; init; } = DefaultResultColor;
    public string CopyColor { get; init; } = DefaultCopyColor;
    public string ClearColor { get; init; } = DefaultResultColor;
    public double IconSize => Math.Round(Math.Clamp(Size, MinimumSize, MaximumSize) * 5d / 9);
    public double MinimumPanelHeight => Math.Max(ScreenLayout.MinimumResultHeight, 3 * Math.Clamp(Size, MinimumSize, MaximumSize) + 30);

    public ResultButtonAppearance Normalize() => this with
    {
        Size = Math.Clamp(Size, MinimumSize, MaximumSize),
        ResultColor = NormalizeColor(ResultColor, DefaultResultColor),
        CopyColor = NormalizeColor(CopyColor, DefaultCopyColor),
        ClearColor = NormalizeColor(ClearColor, DefaultResultColor)
    };

    private static string NormalizeColor(string? color, string fallback) => color is { Length: 7 } &&
        color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit) ? color.ToUpperInvariant() : fallback;
}

internal sealed class ResultButtonAppearanceConverter : JsonConverter<ResultButtonAppearance>
{
    public override bool HandleNull => true;
    public override ResultButtonAppearance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new();
        string Color(string name, string fallback) => root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        return new ResultButtonAppearance
        {
            Size = root.TryGetProperty(nameof(ResultButtonAppearance.Size), out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt32(out var pixels) ? pixels : ResultButtonAppearance.DefaultSize,
            ResultColor = Color(nameof(ResultButtonAppearance.ResultColor), ResultButtonAppearance.DefaultResultColor),
            CopyColor = Color(nameof(ResultButtonAppearance.CopyColor), ResultButtonAppearance.DefaultCopyColor),
            ClearColor = Color(nameof(ResultButtonAppearance.ClearColor), ResultButtonAppearance.DefaultResultColor)
        }.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, ResultButtonAppearance value, JsonSerializerOptions options)
    {
        var normalized = (value ?? new()).Normalize();
        writer.WriteStartObject();
        writer.WriteNumber(nameof(ResultButtonAppearance.Size), normalized.Size);
        writer.WriteString(nameof(ResultButtonAppearance.ResultColor), normalized.ResultColor);
        writer.WriteString(nameof(ResultButtonAppearance.CopyColor), normalized.CopyColor);
        writer.WriteString(nameof(ResultButtonAppearance.ClearColor), normalized.ClearColor);
        writer.WriteEndObject();
    }
}
