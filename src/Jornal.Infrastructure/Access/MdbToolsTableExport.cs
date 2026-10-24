using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

// Общий экспорт одной таблицы; Linux остаётся только для чтения.
internal sealed class MdbToolsTableExport(string? executable = null)
{
    public async Task<T> ReadAsync<T>(string path, string tableName, Func<TextReader, CancellationToken, T> parse,
        CancellationToken cancellationToken = default, char delimiter = ',')
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var start = new ProcessStartInfo(executable ?? Environment.GetEnvironmentVariable("JOURNAL_MDB_EXPORT") ?? "mdb-export")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        var arguments = new[] { "-D", "%Y-%m-%dT%H:%M:%S", "-T", "%Y-%m-%dT%H:%M:%S", "-0", "NULL" }
            .Concat(delimiter == ',' ? [] : new[] { "-d", delimiter.ToString() })
            .Concat([Path.GetFullPath(path), tableName]);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        start.Environment["MDBICONV"] = "UTF-8";
        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new OrderReadException("Для чтения MDB на Linux нужен пакет mdbtools (команда mdb-export). Установите его и повторите подключение.", exception);
        }
        using var cancellation = token.Register(() => Stop(process));
        Task<string>? errors = null;
        try
        {
            errors = process.StandardError.ReadToEndAsync(token);
            var result = await Task.Run(() => parse(process.StandardOutput, token), token);
            // Прайсу нужна только первая строка; остаток stdout всё равно надо вычитать,
            // иначе большой вывод может заблокировать дочерний процесс.
            var buffer = new char[4096];
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), token) != 0) { }
            await process.WaitForExitAsync(token);
            var error = await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
                throw new OrderReadException($"MDB Tools не удалось полностью прочитать {tableName}. Проверьте файл и структуру базы.",
                    new IOException($"mdb-export: exit={process.ExitCode}; {error}"));
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrderReadException("Превышено время чтения MDB. Проверьте доступ к общей папке и повторите обновление.");
        }
        finally
        {
            Stop(process);
            if (errors is not null) try { await errors; } catch (Exception) { }
        }
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var export = executable ?? Environment.GetEnvironmentVariable("JOURNAL_MDB_EXPORT") ?? "mdb-export";
        var sibling = Path.Combine(Path.GetDirectoryName(export) ?? "", "mdb-tables");
        var program = Environment.GetEnvironmentVariable("JOURNAL_MDB_TABLES") ?? (File.Exists(sibling) ? sibling : "mdb-tables");
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-1"); start.ArgumentList.Add(Path.GetFullPath(path)); start.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception exception) { throw new OrderReadException("Для чтения MDB нужен полный пакет mdbtools, включая mdb-tables.", exception); }
        using var cancel = timeout.Token.Register(() => Stop(process));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var names = await output; var errors = await error;
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors))
                throw new OrderReadException("Не удалось проверить таблицы MDB. Время резки не подменяется отсутствующим.");
            return names.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OrderReadException("Превышено время проверки таблиц MDB."); }
        finally
        {
            Stop(process);
            try { await output; } catch (Exception) { }
            try { await error; } catch (Exception) { }
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    internal sealed record CsvField(string Text, bool Quoted);

    internal static IEnumerable<IReadOnlyList<CsvField>> ReadCsv(TextReader reader, CancellationToken cancellationToken,
        char delimiter = ',', string tableName = "Orders")
    {
        // Поля текста всегда заключены mdb-export в кавычки. Поэтому NULL отличается от "NULL" и "".
        while (reader.Peek() >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new List<CsvField>();
            while (true)
            {
                var quoted = reader.Peek() == '"';
                var value = new StringBuilder();
                if (quoted)
                {
                    reader.Read();
                    while (true)
                    {
                        var ch = reader.Read();
                        if (ch == -1) throw new OrderReadException($"Незавершённое текстовое поле {tableName}; данные не загружены.");
                        if (ch == '"')
                        {
                            if (reader.Peek() != '"') break;
                            reader.Read();
                        }
                        value.Append((char)ch);
                    }
                }
                else
                {
                    while (reader.Peek() is var ch && ch != -1 && ch != delimiter && ch is not ('\r' or '\n'))
                        value.Append((char)reader.Read());
                }
                fields.Add(new(value.ToString(), quoted));
                var separator = reader.Read();
                if (separator == delimiter) continue;
                if (separator == '\r') { if (reader.Peek() == '\n') reader.Read(); }
                else if (separator is not (-1 or '\n')) throw new OrderReadException($"Некорректный разделитель в {tableName}; данные не загружены.");
                break;
            }
            yield return fields;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
