using System.Text.RegularExpressions;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WordMapEdit(byte[]? Bytes, int Pages, int ChangedCards);

public interface IWorkflowWordMapEditor
{
    Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token);
}

public sealed class WordMapUnavailableException(string message) : Exception(message);

internal sealed record WordMapCell(int Index, int Row, string Text);

internal static class WorkflowWordMapRules
{
    internal static string Normalize(string text) => Regex.Replace(text, @"[\s\a]+", " ").Trim();

    // Match labels and their neighbours within the same row; merged cells have no fixed column number.
    internal static int[]? Comments(IReadOnlyList<WordMapCell[]> tables, string projectFileName)
    {
        var comments = new List<int>(); var foreign = false;
        foreach (var cells in tables)
        {
            var labels = cells.Select(c => Normalize(c.Text)).ToArray();
            var orders = Enumerable.Range(0, cells.Length).Where(i => labels[i] == "Заказ").ToArray();
            if (orders.Length == 0 || !labels.Contains("Файл УП") || !labels.Contains("Комментарий")) continue;
            if (labels.Take(orders[0]).Any(s => s is "Файл УП" or "Комментарий")) throw BadTable();
            for (var block = 0; block < orders.Length; block++)
            {
                var start = orders[block]; var end = block + 1 < orders.Length ? orders[block + 1] : cells.Length;
                WordMapCell? Value(int index) => index + 1 < end && cells[index + 1].Row == cells[index].Row ? cells[index + 1] : null;
                var up = Enumerable.Range(start + 1, end - start - 1).Where(i => labels[i] == "Файл УП").ToArray();
                var comment = Enumerable.Range(start + 1, end - start - 1).Where(i => labels[i] == "Комментарий").ToArray();
                if (up.Length != 1 || comment.Length != 1 || Value(start) is not { } orderValue ||
                    Value(up[0]) is null || Value(comment[0]) is not { } commentValue) throw BadTable();
                var name = Normalize(orderValue.Text);
                if (name.Length > 0 && WorkflowShopDiscovery.Key(name) != WorkflowShopDiscovery.Key(projectFileName))
                { foreign = true; continue; }
                comments.Add(commentValue.Index);
            }
        }
        if (foreign && comments.Count > 0) throw new WorkflowValidationException("В DOC есть карты разных проектов. Файл не изменён.");
        return comments.Count == 0 ? null : comments.ToArray();
    }

    private static WorkflowValidationException BadTable() => new("Не удалось распознать все блоки карты DOC. Файл не изменён.");

}
