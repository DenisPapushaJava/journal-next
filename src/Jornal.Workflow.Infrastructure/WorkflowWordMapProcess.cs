using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Word runs outside the UI process: a hidden Office prompt or a blocked COM call has a deadline.
public sealed class WorkflowWordMapProcess(string executable, string? assembly = null) : IWorkflowWordMapEditor
{
    public const string Argument = "--workflow-word-map";
    internal sealed record Request(string ProjectFileName, string Caption);
    internal sealed record Response(bool Matched, bool Changed, int Pages, int ChangedCards, string? Error = null, bool Unavailable = false);
    internal sealed record WordProcess(int Id, long StartedUtcTicks);

    public async Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new WordMapUnavailableException("Подписание DOC через Microsoft Word доступно на Windows.");
        if (Type.GetTypeFromProgID("Word.Application") is null)
            throw new WordMapUnavailableException("Microsoft Word не найден. DOC-карты не подписаны.");
        if (source.Length < 8 || !source.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw new WorkflowValidationException("Файл не является картой Word DOC. Проверьте его вручную.");
        var folder = Path.Combine(Path.GetTempPath(), "JournalNext-word-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var worker = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true } };
        if (assembly is not null) worker.StartInfo.ArgumentList.Add(assembly);
        worker.StartInfo.ArgumentList.Add(Argument); worker.StartInfo.ArgumentList.Add(folder);
        var started = false;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "input.doc"), source, token);
            await File.WriteAllTextAsync(Path.Combine(folder, "request.json"), JsonSerializer.Serialize(new Request(projectFileName, caption)), token);
            worker.Start(); started = true;
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var path = Path.Combine(folder, "response.json");
            if (worker.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length > 16384)
                throw new WorkflowValidationException("Не удалось подписать DOC через Word. Исходник не изменён.");
            var response = JsonSerializer.Deserialize<Response>(await File.ReadAllTextAsync(path, token));
            if (response is null) throw new InvalidDataException("Invalid Word response.");
            if (response.Error is { } error)
            {
                if (response.Unavailable) throw new WordMapUnavailableException(error);
                throw new WorkflowValidationException(error);
            }
            if (!response.Matched) return null;
            if (response.Pages is < 1 or > 100 || response.ChangedCards < 0 || response.Changed != (response.ChangedCards > 0))
                throw new InvalidDataException("Invalid Word result.");
            if (!response.Changed) return new(null, response.Pages, 0);
            var output = Path.Combine(folder, "output.doc");
            if (new FileInfo(output).Length is <= 0 or > 50 * 1024 * 1024) throw new InvalidDataException("Invalid DOC output.");
            var bytes = await File.ReadAllBytesAsync(output, token);
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(source.AsSpan(0, 8))) throw new InvalidDataException("Output is not binary DOC.");
            return new(bytes, response.Pages, response.ChangedCards);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WordMapUnavailableException("Word не ответил вовремя. DOC-карта не изменена."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new WordMapUnavailableException("Не удалось запустить обработку DOC через Word. Исходник не изменён."); }
        finally
        {
            if (started)
            {
                try
                {
                    if (!worker.HasExited)
                    {
                        worker.Kill(entireProcessTree: false);
                        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await worker.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
                StopOwnedWord(folder);
            }
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A busy temp copy is safer than touching the working map. */ }
        }
    }

    private static void StopOwnedWord(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "word-process.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return;
            var stamp = JsonSerializer.Deserialize<WordProcess>(File.ReadAllText(path));
            if (stamp is null) return;
            using var process = Process.GetProcessById(stamp.Id);
            // Recorded only after proving that this process did not exist before COM activation.
            if (process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime().Ticks == stamp.StartedUtcTicks &&
                (process.MainWindowHandle == IntPtr.Zero || !IsWindowVisible(process.MainWindowHandle))) process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    // Called from the STA entry point before Avalonia, diagnostics or the updater are initialized.
    public static int RunWorker(string folder)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 1;
            var path = Path.Combine(folder, "request.json");
            if (new FileInfo(path).Length > 16384) return 1;
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path));
            if (request is null || string.IsNullOrWhiteSpace(request.ProjectFileName) || string.IsNullOrWhiteSpace(request.Caption)) return 1;
            var response = WorkflowWordMap.Edit(folder, request);
            File.WriteAllText(Path.Combine(folder, "response.json"), JsonSerializer.Serialize(response));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                var message = ex is WorkflowValidationException or WordMapUnavailableException ? ex.Message : "Не удалось подписать DOC через Word. Исходник не изменён.";
                File.WriteAllText(Path.Combine(folder, "response.json"), JsonSerializer.Serialize(new Response(false, false, 0, 0, message, ex is WordMapUnavailableException)));
                return 0;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { return 1; }
        }
    }
}
