using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WorkflowWordMap
{
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
        internal dynamic Keep(object value) { if (_seen.Add(value)) _objects.Add(value); return value; }
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(_objects[i])) Marshal.FinalReleaseComObject(_objects[i]);
        }
    }

    private sealed record Layout(int Pages, int Tables, int InlineShapes, int Shapes, int Notes, string OutsideText);
    private sealed record CardCells(List<WordMapCell[]> Tables, Dictionary<int, object> Cells);

    internal static WorkflowWordMapProcess.Response Edit(string folder, WorkflowWordMapProcess.Request request)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Word requires STA.");
        var type = Type.GetTypeFromProgID("Word.Application");
        if (type is null) throw new WordMapUnavailableException("Microsoft Word не найден. DOC-карты не подписаны.");
        var existing = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("WINWORD")) { using (p) existing.Add(p.Id); }
        using var scope = new ComScope();
        object application;
        try { application = Activator.CreateInstance(type)!; }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        { throw new WordMapUnavailableException("Не удалось запустить Microsoft Word. DOC-карты не изменены."); }
        dynamic word = scope.Keep(application);
        dynamic? document = null; dynamic? documents = null; var owned = false; dynamic? options = null; object? updateLinks = null;
        try
        {
            documents = scope.Keep(word.Documents);
            // Word.Application has no Hwnd. Use a window of our inert temporary text document,
            // before opening a DOC or changing any application settings.
            var probe = Path.Combine(folder, "window-probe.txt");
            File.WriteAllText(probe, "Word window ownership probe.");
            WorkflowWordMapWindow.Probe(application, (object)documents, probe, value => scope.Keep(value), handle =>
            {
                GetWindowThreadProcessId(new IntPtr(handle), out var pid);
                if (pid == 0 || existing.Contains(checked((int)pid)))
                    throw new WordMapUnavailableException("Не удалось запустить отдельный Word. DOC-карта не изменена.");
                owned = true;
            });
            if ((bool)word.Visible || (int)documents.Count != 0)
                throw new WordMapUnavailableException("Word используется другим окном. DOC-карта не изменена.");
            word.Visible = false; word.DisplayAlerts = 0; word.ScreenUpdating = false;
            word.AutomationSecurity = 3; // msoAutomationSecurityForceDisable; DisplayAlerts does not disable macros.
            options = scope.Keep(word.Options); updateLinks = options.UpdateLinksAtOpen; options.UpdateLinksAtOpen = false;
            document = Open(documents, Path.Combine(folder, "input.doc"), scope);
            if ((int)document.ProtectionType != -1 || (bool)document.TrackRevisions)
                throw new WorkflowValidationException("DOC защищён или содержит запись исправлений. Проверьте карту вручную.");
            CardCells beforeCells = ReadCells(document, scope);
            var comments = WorkflowWordMapRules.Comments(beforeCells.Tables, request.ProjectFileName);
            if (comments is null) return new(false, false, 0, 0);
            Layout before = Inspect(document, beforeCells, comments, scope);
            if (before.Pages is < 1 or > 100) throw new WorkflowValidationException("В DOC больше 100 страниц. Подпишите карту вручную.");
            var changed = 0;
            // Inspect every comment first: do not partially edit a mixed/unsupported document.
            foreach (var index in comments)
            {
                dynamic cell = beforeCells.Cells[index]; dynamic range = scope.Keep(cell.Range);
                var old = WorkflowWordMapRules.Normalize((string)range.Text);
                if (old != WorkflowWordMapRules.Normalize(request.Caption)) WorkflowWordMapRules.RequireReplaceable(old);
            }
            foreach (var index in comments)
            {
                dynamic cell = beforeCells.Cells[index]; dynamic range = scope.Keep(cell.Range);
                if (WorkflowWordMapRules.Normalize((string)range.Text) == WorkflowWordMapRules.Normalize(request.Caption)) continue;
                range.End = (int)range.End - 1; // Keep the end-of-cell marker and the table structure.
                range.Text = request.Caption;
                dynamic text = scope.Keep(document.Range((int)range.Start, (int)range.Start + request.Caption.Length));
                dynamic font = scope.Keep(text.Font);
                var originalSize = (float)font.Size;
                var size = originalSize is >= 8 and <= 12 ? originalSize : 10f;
                for (; size >= 8; size -= .5f)
                {
                    font.Size = size;
                    document.Repaginate();
                    if ((int)text.ComputeStatistics(1) == 1) break; // wdStatisticLines
                }
                if (size < 8) throw new WorkflowValidationException("Подпись не помещается в строку DOC. Сократите реквизиты или подпишите вручную.");
                changed++;
            }
            if (changed == 0) return new(true, false, before.Pages, 0);
            CardCells afterCells = ReadCells(document, scope);
            var afterComments = WorkflowWordMapRules.Comments(afterCells.Tables, request.ProjectFileName)!;
            if (Inspect(document, afterCells, afterComments, scope) != before)
                throw new WorkflowValidationException("После подписи изменилась разметка DOC. Исходник не изменён.");
            var output = Path.Combine(folder, "output.doc");
            document.SaveAs2(FileName: output, FileFormat: 0, AddToRecentFiles: false); // wdFormatDocument97
            document.Close(SaveChanges: 0); document = null;
            document = Open(documents, output, scope);
            CardCells savedCells = ReadCells(document, scope);
            var savedComments = WorkflowWordMapRules.Comments(savedCells.Tables, request.ProjectFileName);
            if (savedComments is null || savedComments.Length != comments.Length || Inspect(document, savedCells, savedComments, scope) != before ||
                savedCells.Tables.SelectMany(t => t).Where(c => savedComments.Contains(c.Index)).Any(c => WorkflowWordMapRules.Normalize(c.Text) != WorkflowWordMapRules.Normalize(request.Caption)))
                throw new WorkflowValidationException("Не удалось проверить сохранённую DOC-карту. Исходник не изменён.");
            return new(true, true, before.Pages, changed);
        }
        finally
        {
            var closedOwnDocument = document is null;
            try { if (document is not null) document.Close(SaveChanges: 0); closedOwnDocument = true; }
            catch (COMException) { }
            try { if (options is not null && updateLinks is not null) options.UpdateLinksAtOpen = updateLinks; }
            catch (COMException) { }
            // Never attach to, hide or quit an instance that existed before this action.
            try
            {
                if (documents is not null)
                    WorkflowWordMapWindow.QuitIfIdle(application, (object)documents, owned, closedOwnDocument);
            }
            catch (COMException) { }
        }
    }

    private static dynamic Open(dynamic documents, string path, ComScope scope) => scope.Keep(documents.Open(
        FileName: path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
        PasswordDocument: Guid.NewGuid().ToString("N"), WritePasswordDocument: Guid.NewGuid().ToString("N"),
        Visible: false, OpenAndRepair: false, NoEncodingDialog: true));

    private static CardCells ReadCells(dynamic document, ComScope scope)
    {
        var result = new CardCells([], []); dynamic tables = scope.Keep(document.Tables);
        if ((int)tables.Count > 2000) throw new WorkflowValidationException("Слишком много таблиц в DOC. Проверьте карту вручную.");
        var next = 0;
        for (var t = 1; t <= (int)tables.Count; t++)
        {
            dynamic table = scope.Keep(tables[t]); dynamic range = scope.Keep(table.Range); dynamic cells = scope.Keep(range.Cells);
            var row = new List<WordMapCell>();
            if ((int)cells.Count + next > 20000) throw new WorkflowValidationException("Слишком большая таблица DOC. Проверьте карту вручную.");
            for (var i = 1; i <= (int)cells.Count; i++)
            {
                dynamic cell = scope.Keep(cells[i]); dynamic content = scope.Keep(cell.Range);
                result.Cells.Add(next, (object)cell);
                row.Add(new(next++, (int)cell.RowIndex, (string)content.Text));
            }
            result.Tables.Add(row.ToArray());
        }
        return result;
    }

    private static Layout Inspect(dynamic document, CardCells cells, int[] comments, ComScope scope)
    {
        var spans = comments.Select(i =>
        {
            dynamic cell = cells.Cells[i]; dynamic range = scope.Keep(cell.Range);
            return (Start: (int)range.Start, End: (int)range.End);
        }).OrderBy(s => s.Start).ToArray();
        dynamic body = scope.Keep(document.Content); var cursor = (int)body.Start; var text = new StringBuilder();
        foreach (var span in spans)
        {
            if (span.Start < cursor) throw new WorkflowValidationException("Неоднозначная структура DOC. Проверьте карту вручную.");
            dynamic part = scope.Keep(document.Range(cursor, span.Start)); text.Append((string)part.Text).Append("<COMMENT>"); cursor = span.End;
        }
        dynamic tail = scope.Keep(document.Range(cursor, (int)body.End)); text.Append((string)tail.Text);
        dynamic inline = scope.Keep(document.InlineShapes); dynamic shapes = scope.Keep(document.Shapes); dynamic notes = scope.Keep(document.Comments);
        return new((int)document.ComputeStatistics(2), cells.Tables.Count, (int)inline.Count, (int)shapes.Count, (int)notes.Count, text.ToString());
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
