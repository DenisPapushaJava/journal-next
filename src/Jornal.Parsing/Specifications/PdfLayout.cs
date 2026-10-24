using Jornal.Parsing.Documents;

namespace Jornal.Parsing.Specifications;

// Геометрия текста, не зависящая от порядка PDF-объектов и абсолютных координат шаблона.
internal static class PdfLayout
{
    internal sealed record Line(IReadOnlyList<PdfWord> Words)
    {
        public double Center => Words.Average(CenterY);
        public double Top => Words.Max(word => word.Top);
        public double Bottom => Words.Min(word => word.Bottom);
        public string Text => Join(Words);
    }

    public static double CenterY(PdfWord word) => (word.Top + word.Bottom) / 2;
    public static double CenterX(PdfWord word) => (word.Left + word.Right) / 2;
    public static double Tolerance(IEnumerable<PdfWord> words)
    {
        var heights = words.Select(word => word.Top - word.Bottom).Where(height => height > 0).Order().ToArray();
        return heights.Length == 0 ? 1 : Math.Max(0.01, heights[heights.Length / 2] * 0.55);
    }

    public static IReadOnlyList<Line> Lines(IEnumerable<PdfWord> source, double tolerance)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var word in source.Where(word => !string.IsNullOrWhiteSpace(word.Text)).OrderByDescending(CenterY).ThenBy(word => word.Left))
        {
            var line = lines.LastOrDefault();
            if (line is null || Math.Abs(line.Average(CenterY) - CenterY(word)) > tolerance)
                lines.Add(line = []);
            line.Add(word);
        }
        return lines.Select(words => new Line(words.OrderBy(word => word.Left).ToArray())).ToArray();
    }

    public static string Join(IEnumerable<PdfWord> words) => string.Join(" ", words.OrderBy(word => word.Left).Select(word => word.Text.Trim()));
    public static PdfValueSource Evidence(int page, string label, IReadOnlyList<PdfWord> words, IReadOnlyList<PdfWord>? fallback = null)
    {
        var bounds = words.Count > 0 ? words : fallback ?? [];
        return new(page, bounds.Count == 0 ? 0 : bounds.Min(word => word.Left), bounds.Count == 0 ? 0 : bounds.Min(word => word.Bottom),
            bounds.Count == 0 ? 0 : bounds.Max(word => word.Right), bounds.Count == 0 ? 0 : bounds.Max(word => word.Top), label,
            string.Join("\n", Lines(words, Tolerance(words)).Select(line => line.Text)));
    }
}
