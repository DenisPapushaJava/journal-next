using System.Diagnostics;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Word runs outside the UI process: a hidden Office prompt or a blocked COM call has a deadline.
public sealed class WorkflowWordMapProcess(string executable, string? assembly = null) : IWorkflowWordMapEditor
{
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    public const string Argument = "--workflow-word-map";
    internal sealed record Request(string ProjectFileName, string Caption, bool InspectOnly = false, WordMapMatch? Match = null)
    {
        public ShopMapBundle Bundle { get; init; }
        public string[]? BundleSources { get; init; }
    }
    internal sealed record Response(bool Matched, bool Changed, int Pages, int ChangedCards, string? Error = null, bool Unavailable = false, WordMapReference[]? References = null);

    public async Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
        => await PrepareCoreAsync(source, new(projectFileName, caption), token);
    public Task<WordMapEdit?> PrepareMatchedAsync(byte[] source, string projectFileName, string caption, WordMapMatch match, CancellationToken token) =>
        PrepareCoreAsync(source, new(projectFileName, caption, Match: match), token);
    private async Task<WordMapEdit?> PrepareCoreAsync(byte[] source, Request request, CancellationToken token)
    {
        var result = await ExecuteAsync(source, request, token);
        return result.Edit;
    }
    public async Task<WordMapReference[]?> InspectAsync(byte[] source, CancellationToken token)
    {
        var result = await ExecuteAsync(source, new("inspect", "inspect", InspectOnly: true), token);
        return result.References;
    }
    public async Task BuildBundleAsync(string folder, string[] sourceNames, ShopMapBundle format, CancellationToken token, WordMapMatch? managerProject = null)
    {
        EnsureAvailable();
        var response = await InvokeAsync(folder, new("", "", Match: managerProject) { Bundle = format, BundleSources = sourceNames },
            "Не удалось подготовить файл карт раскроя HansLaser через Word.", token);
        if (!response.Matched || response.Pages < 1 ||
            !File.Exists(Path.Combine(folder, WorkflowShopTransfer.BundleFileName(format))))
            throw new WorkflowValidationException("Word не вернул готовый файл карт раскроя HansLaser.");
    }
    private static void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("Word.Application") is null)
            throw new WordMapUnavailableException("Обработка DOC-карт HansLaser требует Microsoft Word на Windows.");
    }
    private async Task<(WordMapEdit? Edit, WordMapReference[]? References)> ExecuteAsync(byte[] source, Request request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (source.Length < 8 || !source.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw new WorkflowValidationException("Файл не является картой Word DOC. Проверьте его вручную.");
        var folder = Path.Combine(Path.GetTempPath(), "JournalNext-word-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "input.doc"), source, token);
            var response = await InvokeAsync(folder, request, "Не удалось подписать DOC через Word. Исходник не изменён.", token);
            if (request.InspectOnly)
            {
                if (response.Changed || response.References is null || response.References.Length > 2000 || response.References.Any(r => r is null || r.Order is null || r.Program is null))
                    throw new InvalidDataException("Invalid Word inspection.");
                return (null, response.References);
            }
            if (!response.Matched) return (null, null);
            if (response.Pages is < 1 or > 100 || response.ChangedCards < 0 || response.Changed != (response.ChangedCards > 0))
                throw new InvalidDataException("Invalid Word result.");
            if (!response.Changed) return (new(null, response.Pages, 0), null);
            var output = Path.Combine(folder, "output.doc");
            if (new FileInfo(output).Length is <= 0 or > 50 * 1024 * 1024) throw new InvalidDataException("Invalid DOC output.");
            var bytes = await File.ReadAllBytesAsync(output, token);
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(source.AsSpan(0, 8))) throw new InvalidDataException("Output is not binary DOC.");
            return (new(bytes, response.Pages, response.ChangedCards), null);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A busy temp copy is safer than touching the working map. */ }
        }
    }

    private async Task<Response> InvokeAsync(string folder, Request request, string failure, CancellationToken token)
    {
        await _workerGate.WaitAsync(token).ConfigureAwait(false);
        try { return await InvokeWorkerAsync(folder, request, failure, token).ConfigureAwait(false); }
        finally { _workerGate.Release(); }
    }

    private async Task<Response> InvokeWorkerAsync(string folder, Request request, string failure, CancellationToken token)
    {
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (requestBytes.Length > 1024 * 1024) throw new WorkflowValidationException("Слишком большой список файлов для обработки Word. Проверьте папку проекта.");
        await File.WriteAllBytesAsync(Path.Combine(folder, "request.json"), requestBytes, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(300, 90 + 3L * (request.BundleSources?.Length ?? 0))));
        using var worker = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true } };
        if (assembly is not null) worker.StartInfo.ArgumentList.Add(assembly);
        worker.StartInfo.ArgumentList.Add(Argument); worker.StartInfo.ArgumentList.Add(folder);
        var started = false;
        try
        {
            token.ThrowIfCancellationRequested();
            worker.Start(); started = true;
            // A window can close before the async continuation runs.
            using var stopOnCancel = token.UnsafeRegister(static state => StopWorker((Process)state!), worker);
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, "response.json");
            if (worker.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                throw new WorkflowValidationException(failure);
            var response = JsonSerializer.Deserialize<Response>(await File.ReadAllTextAsync(path, token));
            if (response is null) throw new InvalidDataException("Invalid Word response.");
            if (response.Error is { } error)
            {
                if (response.Unavailable) throw new WordMapUnavailableException(error);
                throw new WorkflowValidationException(error);
            }
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WordMapUnavailableException("Word не ответил вовремя. Закройте окна Word, требующие ответа, и повторите операцию."); }
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
                        StopWorker(worker);
                        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await worker.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            }
        }
    }

    private static void StopWorker(Process worker)
    {
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // Called from the STA entry point before Avalonia, diagnostics or the updater are initialized.
    public static int RunWorker(string folder)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 1;
            var path = Path.Combine(folder, "request.json");
            if (new FileInfo(path).Length > 1024 * 1024) return 1;
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path));
            if (request is null || !Enum.IsDefined(request.Bundle)) return 1;
            if (request.Bundle == ShopMapBundle.Separate && (string.IsNullOrWhiteSpace(request.ProjectFileName) || string.IsNullOrWhiteSpace(request.Caption))) return 1;
            if (request.Bundle != ShopMapBundle.Separate && request.BundleSources is not { Length: > 0 }) return 1;
            var response = WorkflowWordMap.Edit(folder, request);
            File.WriteAllText(Path.Combine(folder, "response.json"), JsonSerializer.Serialize(response));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                var message = ex is WorkflowValidationException or WordMapUnavailableException ? ex.Message : "Не удалось обработать карты раскроя через Word.";
                File.WriteAllText(Path.Combine(folder, "response.json"), JsonSerializer.Serialize(new Response(false, false, 0, 0, message, ex is WordMapUnavailableException)));
                return 0;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { return 1; }
        }
    }
}
