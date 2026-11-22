using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowOfficeStorageSettings(string Address, Guid DatabaseId, Guid CredentialId, bool CanWrite)
{
    internal string CredentialPath(string directory) => Path.Combine(directory, $"office-storage-{CredentialId:N}.json");
}

internal sealed partial class WorkflowOfficeStorageClient(WorkflowOfficeStorageSettings settings, string token, string profileDirectory,
    HttpClient http, HttpClient? fileHttp = null) : IWorkflowAccessTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient StorageTransport = WorkflowOfficePreviewClient.CreateTransport(TimeSpan.FromSeconds(55));
    private readonly Uri _server = WorkflowOfficePreviewClient.RequireServer(settings.Address);
    private sealed record ConnectionResult(Guid DatabaseId, string Token, bool CanWrite);
    private sealed record ServerError(string? Error, bool Conflict, bool OutcomeUnknown);
    internal WorkflowOfficeStorageSettings Settings => settings;
    internal WorkflowMdbStore CreateStore() =>
        new(Path.Combine(profileDirectory, $"office-{settings.DatabaseId:N}.mdb"), this, settings.DatabaseId);

    internal static async Task<WorkflowOfficeStorageClient> ConnectAsync(string? address, string? key,
        string profileDirectory, CancellationToken cancellation)
    {
        var server = WorkflowOfficePreviewClient.RequireServer(address);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/v1/storage-connect"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowOfficePreviewClient.RequireAdminKey(key));
        using var response = await SendAsync(WorkflowOfficePreviewClient.Transport, request, cancellation);
        await CheckAsync(response, cancellation, connecting: true);
        var result = await ReadAsync<ConnectionResult>(response, cancellation);
        if (result.DatabaseId == Guid.Empty || !WorkflowOfficeConnection.IsStorageToken(result.Token))
            throw new InvalidDataException("Сервер вернул неполные данные подключения.");
        return new(new(server.AbsoluteUri, result.DatabaseId, Guid.NewGuid(), result.CanWrite), result.Token,
            profileDirectory, StorageTransport);
    }

    internal static WorkflowOfficeStorageClient Open(WorkflowOfficeStorageSettings settings, string directory)
    {
        var key = WorkflowOfficeConnection.Load(settings.CredentialPath(directory));
        if (key is null || key.Address != settings.Address || !WorkflowOfficeConnection.IsStorageToken(key.Token))
            throw new WorkflowValidationException("Ключ удалённого подключения недоступен. Подключитесь к серверу заново.");
        return new(settings, key.Token, directory, StorageTransport);
    }

    internal void SaveCredential() => new WorkflowOfficeConnection(settings.Address, token).Save(settings.CredentialPath(profileDirectory));

    public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken cancellation = default)
    {
        var write = request.Operation == WorkflowAccessOperation.Apply;
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        using var message = Request(HttpMethod.Post, "api/v1/storage/database");
        message.Content = JsonContent.Create(request with { Path = "", DatabaseId = settings.DatabaseId }, options: JsonOptions);
        try
        {
            using var response = await SendAsync(http, message, deadline.Token);
            await CheckAsync(response, deadline.Token);
            var data = await ReadAsync<WorkflowAccessResponse>(response, deadline.Token);
            if (data.Protocol != 1 || data.DatabaseId != settings.DatabaseId || data.Orders is null)
                throw new InvalidDataException("Получен ответ другой MDB.");
            return data;
        }
        catch (Exception ex) when (write && ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        { throw new WorkflowOutcomeUnknownException(ex); }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        { throw new IOException("Сервер не ответил вовремя. Повторите чтение заявок.", ex); }
        catch (HttpRequestException ex) { throw new IOException("Сервер недоступен. Проверьте подключение ZeroTier.", ex); }
    }

    private HttpRequestMessage Request(HttpMethod method, string path) => new(method, new Uri(_server, path))
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellation) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, cancellation);
        return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw new InvalidDataException("Сервер вернул пустой ответ.");
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken cancellation, bool connecting = false)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new WorkflowValidationException(connecting ? "Сервер не принял ключ администратора." :
                "Ключ подключения истёк. Подключитесь к серверу заново в настройках.");
        if (response.StatusCode == HttpStatusCode.NotFound && connecting)
            throw new WorkflowValidationException("Обновите «Сервер заявок» до версии с удалённым хранилищем.");
        ServerError? problem = null;
        try
        {
            await response.Content.LoadIntoBufferAsync(4096, cancellation);
            problem = await response.Content.ReadFromJsonAsync<ServerError>(cancellation);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException) { }
        if (problem?.OutcomeUnknown == true) throw new WorkflowOutcomeUnknownException();
        if (problem?.Conflict == true) throw new WorkflowConflictException();
        throw new WorkflowValidationException(problem?.Error switch
        {
            "remote-writes-disabled" => "Удалённое сохранение выключено в панели сервера.",
            "file-version-conflict" => "Файл изменился на сервере. Локальная копия сохранена.",
            "previous-version-needs-review" or "file-write-unconfirmed" => "Сервер не подтвердил замену файла. Проверьте обе версии.",
            "project-folder-unavailable" => "Рабочая папка проекта недоступна на сервере.",
            "operation-rejected" => "Сервер не смог обработать файлы проекта. Проверьте папку проекта или журнал сервера.",
            "operation-unavailable" => "Файлы проекта временно недоступны на сервере. Проверьте подключение и повторите действие.",
            { } message => message,
            _ => $"Не удалось обратиться к серверу (HTTP {(int)response.StatusCode})."
        });
    }
}
