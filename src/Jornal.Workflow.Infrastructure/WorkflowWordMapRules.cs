using System.Text.RegularExpressions;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WordMapEdit(byte[]? Bytes, int Pages, int ChangedCards);
public sealed record WordMapReference(string Order, string Program);
public sealed record WordMapMatch(string[] CurrentProjects, string[] OtherProjects, bool AllowUnknownOrder = false);

public interface IWorkflowWordMapEditor
{
    Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token);
    Task<WordMapReference[]?> InspectAsync(byte[] source, CancellationToken token) => Task.FromResult<WordMapReference[]?>(null);
    Task<WordMapEdit?> PrepareMatchedAsync(byte[] source, string projectFileName, string caption, WordMapMatch match, CancellationToken token) =>
        PrepareAsync(source, projectFileName, caption, token);
}

public sealed class WordMapUnavailableException(string message) : Exception(message);

internal sealed record WordMapCell(int Index, int Row, string Text);

internal static class WorkflowWordMapRules
{
    internal static string Normalize(string text) => Regex.Replace(text, @"[\s\a]+", " ").Trim();

    // Match labels and their neighbours within the same row; merged cells have no fixed column number.
    internal static string ProjectKey(string value)
    {
        var key = WorkflowShopDiscovery.Key(Normalize(value));
        return key.EndsWith(".DSP", StringComparison.Ordinal) ? key[..^4] : key;
    }
    internal static string ProgramKey(string value)
    {
        var key = WorkflowShopDiscovery.Key(Normalize(value));
        return key.EndsWith(".GNC", StringComparison.Ordinal) ? key[..^4] : key;
    }
    private sealed record Card(int CommentIndex, WordMapReference Reference);
    internal static WordMapReference[] References(IReadOnlyList<WordMapCell[]> tables) => Cards(tables).Select(c => c.Reference).ToArray();
    private static Card[] Cards(IReadOnlyList<WordMapCell[]> tables)
    {
        var cards = new List<Card>();
        foreach (var cells in tables)
        {
            var labels = cells.Select(c => Normalize(c.Text).TrimEnd(':').ToUpperInvariant()).ToArray();
            var orders = Enumerable.Range(0, cells.Length).Where(i => labels[i] == "ЗАКАЗ").ToArray();
            if (orders.Length == 0 || !labels.Contains("ФАЙЛ УП") || !labels.Contains("КОММЕНТАРИЙ")) continue;
            if (labels.Take(orders[0]).Any(s => s is "ФАЙЛ УП" or "КОММЕНТАРИЙ")) throw BadTable();
            for (var block = 0; block < orders.Length; block++)
            {
                var start = orders[block]; var end = block + 1 < orders.Length ? orders[block + 1] : cells.Length;
                WordMapCell? Value(int index) => index + 1 < end && cells[index + 1].Row == cells[index].Row ? cells[index + 1] : null;
                var up = Enumerable.Range(start + 1, end - start - 1).Where(i => labels[i] == "ФАЙЛ УП").ToArray();
                var comment = Enumerable.Range(start + 1, end - start - 1).Where(i => labels[i] == "КОММЕНТАРИЙ").ToArray();
                if (up.Length != 1 || comment.Length != 1 || Value(start) is not { } orderValue ||
                    Value(up[0]) is not { } programValue || Value(comment[0]) is not { } commentValue) throw BadTable();
                cards.Add(new(commentValue.Index, new(Normalize(orderValue.Text), Normalize(programValue.Text))));
            }
        }
        return cards.ToArray();
    }
    internal static int[]? Comments(IReadOnlyList<WordMapCell[]> tables, string projectFileName, WordMapMatch? match = null)
    {
        var comments = new List<int>(); var foreign = false;
        var current = (match?.CurrentProjects ?? [projectFileName]).Select(ProjectKey).ToHashSet();
        var others = (match?.OtherProjects ?? []).Select(ProjectKey).ToHashSet();
        foreach (var card in Cards(tables))
        {
            var key = ProjectKey(card.Reference.Order);
            if (!current.Contains(key) && (others.Contains(key) || key.Length > 0 && match?.AllowUnknownOrder != true))
            { foreign = true; continue; }
            comments.Add(card.CommentIndex);
        }
        if (foreign && comments.Count > 0) throw new WorkflowValidationException("В DOC есть карты разных проектов. Файл не изменён.");
        return comments.Count == 0 ? null : comments.ToArray();
    }

    private static WorkflowValidationException BadTable() => new("Не удалось распознать все блоки карты DOC. Файл не изменён.");

}
