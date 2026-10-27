using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed class WorkflowAccessClient(string? executable = null, IWorkflowAccessTransport? process = null) : IWorkflowAccessTransport
{
    public static string DefaultExecutable => AccessHostClient.DefaultExecutable;
    public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task<WorkflowAccessResponse> Send(WorkflowAccessRequest value, CancellationToken cancellation) =>
            process?.SendAsync(value, cancellation) ?? SendProcessAsync(value, cancellation);
        if (request.Operation is not (WorkflowAccessOperation.Inspect or WorkflowAccessOperation.Read or
            WorkflowAccessOperation.ReadOrders or WorkflowAccessOperation.ReadTrash))
            return await Send(request, token).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var response = await Send(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            // A new process has a fresh Jet cache. Read only metadata to reject any multi-table snapshot
            // assembled across another client's commit; Jet supports only ReadCommitted transactions.
            var revision = await Send(new(1, WorkflowAccessOperation.ReadRevision, request.Path, response.DatabaseId), deadline.Token).ConfigureAwait(false);
            if (revision.DatabaseId != response.DatabaseId || revision.Revision != response.Revision ||
                revision.CatalogVersion != response.CatalogVersion) throw new WorkflowConflictException();
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Модуль MDB не ответил вовремя. Проверьте доступность общей папки."); }
    }

    private async Task<WorkflowAccessResponse> SendProcessAsync(WorkflowAccessRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Работа с MDB доступна на Windows.");
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > 32 * 1024 * 1024) throw new WorkflowValidationException("Пакет MDB слишком велик. Сохраняйте проекты меньшими группами.");
        var write = request.Operation is WorkflowAccessOperation.Create or WorkflowAccessOperation.Apply or WorkflowAccessOperation.Purge or WorkflowAccessOperation.RecordShopHandoff;
        var dispatched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.GetFullPath(executable ?? DefaultExecutable))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("--workflow");
        Task? input = null, error = null, exit = null; Task<WorkflowAccessResponse?>? output = null;
        try
        {
            process.Start();
            output = JsonSerializer.DeserializeAsync<WorkflowAccessResponse>(new LimitedReadStream(process.StandardOutput.BaseStream, 32 * 1024 * 1024), cancellationToken: deadline.Token).AsTask();
            error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, 4096, deadline.Token);
            exit = process.WaitForExitAsync(deadline.Token);
            dispatched = true; input = WriteAsync();
            await WithReaders(input, output, error).ConfigureAwait(false);
            await WithReaders(exit, output, error).ConfigureAwait(false);
            if (AccessHostBlockedException.IsExitCode(process.ExitCode)) throw new AccessHostBlockedException(process.ExitCode);
            var response = await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || response is null || response.Protocol != 1) throw new InvalidDataException("Некорректный ответ модуля MDB.");
            if (response.Error is not null)
            {
                if (response.OutcomeUnknown) throw new WorkflowOutcomeUnknownException();
                if (response.Conflict) throw new WorkflowConflictException();
                throw new WorkflowValidationException(response.Error);
            }
            if (response.OutcomeUnknown || response.Conflict || response.DatabaseId == Guid.Empty || response.Orders is null || response.Revision < 0 || response.CatalogVersion < 0 ||
                request.DatabaseId is { } expected && response.DatabaseId != expected)
                throw new InvalidDataException("Ответ MDB не соответствует подключённой базе.");
            return response;

            async Task WriteAsync()
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
        }
        catch (Exception e) when (e is WorkflowValidationException or WorkflowConflictException or WorkflowOutcomeUnknownException or AccessHostBlockedException) { throw; }
        catch (System.ComponentModel.Win32Exception) when (!dispatched)
        { throw new WorkflowValidationException("Не удалось запустить модуль MDB. Проверьте установку программы «Заявки» и ограничения запуска программ на этом компьютере."); }
        catch (Exception e) when (write && dispatched && e is not OutOfMemoryException) { throw new WorkflowOutcomeUnknownException(e); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Модуль MDB не ответил вовремя. Проверьте доступность общей папки."); }
        finally
        {
            var cancellation = deadline.CancelAsync();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try
            {
                await Task.WhenAll(new[] { input, output, error, exit, cancellation }.Where(t => t is not null).Select(async t => { try { await t!.ConfigureAwait(false); } catch (Exception) { } }))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException) { /* No automatic restart/replay of a write. */ }
        }
    }
    private static async Task WithReaders(Task operation, Task output, Task error)
    {
        var tasks = new List<Task> { operation, output, error };
        while (!operation.IsCompleted) { var done = await Task.WhenAny(tasks).ConfigureAwait(false); if (operation.IsCompleted) break; await done.ConfigureAwait(false); tasks.Remove(done); }
        await operation.ConfigureAwait(false);
    }
    private sealed class LimitedReadStream(Stream source, long maximum) : Stream
    {
        private long _count;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = source.Read(buffer, offset, count); Count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); Count(read); return read; }
        private void Count(int read) { _count += read; if (_count > maximum) throw new InvalidDataException("Ответ MDB слишком велик."); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
