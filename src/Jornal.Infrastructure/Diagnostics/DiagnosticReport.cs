using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jornal.Core.Orders;
using Jornal.Infrastructure.Local;

namespace Jornal.Infrastructure.Diagnostics;

public enum DiagnosticOperation
{
    Startup, OrderRead, OrderCreate, OrderEdit, OrderBatch, OrderDelete, OrderRestore, Trash,
    PriceRead, SpecificationImport, Clipboard, Preferences, Update, AccessHost, Unexpected
}
public enum DiagnosticComponent { Desktop, AccessHost }
public enum DiagnosticSeverity { Warning, Error, Fatal }
public sealed record DiagnosticException(string Type, int HResult, string[] Frames, int? NativeCode = null);
public sealed record DiagnosticBreadcrumb(DiagnosticOperation Operation, DateTimeOffset At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagnosticReadAction Action = DiagnosticReadAction.Unknown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagnosticReadOutcome Outcome = DiagnosticReadOutcome.Unknown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMilliseconds = null);

// Только разрешённые поля. Message, Data, пути, исходные документы и параметры операций сюда не передаются.
public sealed record DiagnosticReport(int Schema, string EventId, string SessionId, DateTimeOffset CreatedUtc,
    string Version, string Platform, string OsVersion, string Architecture, string RuntimeVersion,
    DiagnosticComponent Component, DiagnosticOperation Operation, DiagnosticSeverity Severity,
    OrderWriteFailure? WriteFailure, DiagnosticException[] Exceptions, DiagnosticBreadcrumb[] Recent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuildCommit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BuildDirty = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DiagnosticStage Stage = DiagnosticStage.Unknown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticReadContext? ReadContext = null)
{
    public const int MaximumBytes = 32 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    [JsonIgnore] public string Code => "JR-" + EventId[..8].ToUpperInvariant();
    [JsonIgnore] public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', new[] { Component.ToString(), Operation.ToString() }.Concat(Exceptions.Select(item =>
            $"{item.Type}|{item.HResult}|{string.Join(';', item.Frames)}"))))));

    public static DiagnosticReport Create(Exception exception, DiagnosticOperation operation, string sessionId,
        string version, DiagnosticSeverity severity = DiagnosticSeverity.Error,
        DiagnosticComponent component = DiagnosticComponent.Desktop, string? eventId = null,
        IEnumerable<DiagnosticBreadcrumb>? recent = null)
    {
        var errors = new List<DiagnosticException>();
        for (Exception? current = exception; current is not null && errors.Count < 4; current = current.InnerException)
        {
            var type = SafeSymbol(current.GetType().FullName) ?? "System.Exception";
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames()
                .Select(frame => frame.GetMethod()).Where(method => method?.DeclaringType is not null)
                .Select(method => SafeSymbol(method!.DeclaringType!.FullName + "." + method.Name))
                .OfType<string>().Take(12).ToArray();
            errors.Add(new(type, current.HResult, frames, current is Access.AccessHostBlockedException blocked ? blocked.Code :
                current is System.ComponentModel.Win32Exception native ? native.NativeErrorCode :
                (uint)current.HResult >> 16 == 0x8007 ? current.HResult & 0xffff : null));
        }
        return new(1, eventId ?? Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow,
            IsVersion(version) ? version : "unknown", OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
            component, operation, severity, (exception as OrderWriteException)?.Failure, errors.ToArray(), recent?.TakeLast(16).ToArray() ?? [],
            AppDiagnostics.BuildCommit, AppDiagnostics.BuildDirty, DiagnosticStages.Of(exception), DiagnosticReads.ContextOf(exception));
    }

    public void Validate()
    {
        if (Schema != 1 || !Regex.IsMatch(EventId, "^[a-f0-9]{32}$") || !Regex.IsMatch(SessionId, "^[a-f0-9]{32}$") ||
            !IsVersion(Version) || Platform is not ("Windows" or "Linux" or "Other") ||
            !Regex.IsMatch(OsVersion, "^[0-9.]{1,40}$") || !Regex.IsMatch(RuntimeVersion, "^[0-9.]{1,40}$") ||
            Architecture is not ("X64" or "X86" or "Arm64" or "Arm" or "Wasm" or "S390x" or "LoongArch64" or "Armv6" or "Ppc64le" or "RiscV64") ||
            !Enum.IsDefined(Component) || !Enum.IsDefined(Operation) || !Enum.IsDefined(Severity) || !Enum.IsDefined(Stage) ||
            BuildCommit is not null && !Regex.IsMatch(BuildCommit, "^([a-f0-9]{40}|[a-f0-9]{64})$") ||
            BuildDirty is not null && BuildCommit is null ||
            WriteFailure is { } failure && !Enum.IsDefined(failure) || Exceptions is not { Length: > 0 and <= 4 } || Recent is not { Length: <= 16 })
            throw new InvalidDataException("Несовместимый диагностический отчёт.");
        foreach (var item in Exceptions)
            if (item is null || SafeSymbol(item.Type) is null || item.Frames is not { Length: <= 12 } || item.Frames.Any(frame => SafeSymbol(frame) is null))
                throw new InvalidDataException("Недопустимые технические сведения.");
        ReadContext?.Validate();
        if (Recent.Any(item => item is null || !Enum.IsDefined(item.Operation) || !DiagnosticReads.ValidBreadcrumb(item)))
            throw new InvalidDataException("Недопустимая история операций.");
    }

    [JsonIgnore] internal bool HasReadDetails => ReadContext is not null || Recent.Any(item => item.Action != DiagnosticReadAction.Unknown || item.Outcome != DiagnosticReadOutcome.Unknown || item.DurationMilliseconds is not null);
    internal DiagnosticReport WithoutReadDetails() => this with { ReadContext = null,
        Recent = Recent.Select(item => new DiagnosticBreadcrumb(item.Operation, item.At)).ToArray() };

    private static bool IsVersion(string? value) => value is not null && Regex.IsMatch(value, "^[a-zA-Z0-9.+-]{1,100}$");
    private static string? SafeSymbol(string? value) => value is { Length: > 0 and <= 240 } &&
        (value.StartsWith("System.", StringComparison.Ordinal) || value.StartsWith("Jornal.", StringComparison.Ordinal) ||
         value.StartsWith("Avalonia.", StringComparison.Ordinal) || value.StartsWith("Microsoft.", StringComparison.Ordinal) ||
         value.StartsWith("AngleSharp.", StringComparison.Ordinal) || value.StartsWith("UglyToad.PdfPig.", StringComparison.Ordinal)) &&
        Regex.IsMatch(value, "^[A-Za-z0-9_.+<>`]+$") ? value : null;
}

public static class AppDiagnostics
{
    private static readonly object ReportKey = new();
    private sealed record Captured(string Code, bool Fatal);
    public static DiagnosticService? Current { get; set; }
    public static string? Capture(DiagnosticOperation operation, Exception exception, bool fatal = false)
    {
        if (!fatal && (exception is OperationCanceledException or ArgumentException or Jornal.Core.Pricing.PriceCalculationException ||
            exception is OrderWriteException { Failure: OrderWriteFailure.Conflict })) return null;
        try
        {
            if (exception.Data[ReportKey] is Captured captured && (!fatal || captured.Fatal)) return captured.Code;
            var result = Current?.Capture(operation, exception, fatal);
            if (result is not null) exception.Data[ReportKey] = new Captured(result, fatal);
            return result;
        }
        catch (Exception) { return null; }
    }
    public static T Attach<T>(T exception, DiagnosticReport? report) where T : Exception
    {
        if (report is not null)
        {
            exception.Data[ReportKey] = new Captured(report.Code, report.Severity == DiagnosticSeverity.Fatal);
            DiagnosticStages.Tag(exception, report.Stage);
        }
        return exception;
    }
    public static string WithCode(string message, string? code) => code is null ? message : message + "\nКод: " + code;
    public static string Describe(DiagnosticOperation operation, Exception exception, string fallback)
    {
        var code = Capture(operation, exception);
        if (LocalJournalBusyException.Contains(exception))
            return operation is DiagnosticOperation.OrderCreate or DiagnosticOperation.OrderEdit
                ? "База данных сейчас занята. Правки остались в форме. Повторите сохранение через несколько секунд."
                : exception.Message;
        return WithCode(exception is OrderWriteException or OrderReadException or Jornal.Core.Pricing.PriceReadException or
            Jornal.Core.Pricing.PriceCalculationException or ArgumentException ? exception.Message : fallback, code);
    }

    public static string AppVersion => typeof(AppDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string? BuildCommit { get; } = Metadata("Journal.BuildCommit") is { } commit &&
        Regex.IsMatch(commit, "^([a-f0-9]{40}|[a-f0-9]{64})$") ? commit : null;
    public static bool? BuildDirty { get; } = BuildCommit is not null && bool.TryParse(Metadata("Journal.BuildDirty"), out var dirty) ? dirty : null;
    private static string? Metadata(string key) => typeof(AppDiagnostics).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == key)?.Value;
}
