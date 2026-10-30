using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowResultGeneration(IWorkflowWorkspaceStore Source, Guid OrderId, long Version,
    string PreviousText, string Text, bool NeedsConfirmation);

public partial class WorkflowViewModel
{
    private string? _generatedResultText;
    private long _resultTextRevision;
    private string _resultFeedback = "";
    private bool _resultFeedbackError, _isCopyingResult;
    private bool _resultFeedbackTransient;
    private long _resultFeedbackVersion;
    private static readonly IBrush ResultLightPaper = new ImmutableSolidColorBrush(Color.Parse("#F1F2F4"));
    private static readonly IBrush ResultDarkPaper = new ImmutableSolidColorBrush(Color.Parse("#202124"));
    private ResultTextStyle _resultStyle = new();
    private sealed record ClearedResult(IWorkflowWorkspaceStore Source, Guid OrderId, long OrderVersion, long Revision,
        string Text, string? Generated, bool Stale);
    private ClearedResult? _clearedResult;

    public bool IsResultTab => OrderTabIndex == 1;
    public bool CanEditResult => CanUse && IsCardVisible && !_isCopyingResult && !IsCopyingCuttingTask;
    private string? ResultGenerationProblem => !CanEditResult ? "Дождитесь завершения операции или откройте заявку." :
        CardActionProblem ?? (HasCurrentCardEdits || HasPendingReportEdits ? "Сохраните или отмените изменения перед формированием результата." :
        CurrentOrder?.HasProjectRename == true ? "Завершите изменение параметров проекта." :
        HasUnselectedVariant ? "Выберите актуальный вариант для каждой включённой позиции." :
        CurrentOrder?.PendingProjectCount > 0 ? "Загрузите спецификации и сохраните расчёты всех включённых позиций." :
        CurrentOrder?.ActiveSpecifications.Any() != true ? "Добавьте или включите проект и сохраните его расчёт." : null);
    public bool CanGenerateResult => ResultGenerationProblem is null;
    public bool CanOpenResult => CanEditResult && (ResultText.Length > 0 || CanGenerateResult);
    public string GenerateResultHint => ResultGenerationProblem ?? "Сформировать результат по сохранённым расчётам";
    public string OpenResultHint => CanEditResult && ResultText.Length > 0 ? "Открыть текущий результат с ручными правками" : GenerateResultHint;
    public bool CanCopyResult => CanEditResult && !string.IsNullOrWhiteSpace(ResultText);
    public bool CanClearResult => CanEditResult && ResultText.Length > 0;
    public string CopyResultHint => !CanEditResult ? "Дождитесь завершения операции или откройте заявку." :
        !CanCopyResult ? "Сформируйте или введите текст результата." : "Копировать весь текущий текст с размером шрифта и цветом";
    public string ClearResultHint => !CanEditResult ? "Дождитесь завершения операции или откройте заявку." :
        CanClearResult ? "Очистить текст результата с возможностью отмены" : "В результате нет текста для очистки.";
    public bool CanUndoClearResult => CanEditResult && ResultText.Length == 0 && _clearedResult is { } undo &&
        ReferenceEquals(undo.Source, _store) && CurrentOrder?.Id == undo.OrderId && undo.Revision == _resultTextRevision;
    public string GenerateResultCaption => ResultText.Length == 0 ? "Сформировать" : "Сформировать заново";
    public string ResultStatus => (_resultFeedback.Length == 0 && ResultText.Length == 0 && !ResultIsStale ? ResultGenerationProblem ?? "" : _resultFeedback) + (ResultIsStale ?
        (_resultFeedback.Length > 0 ? " · " : "") + "Данные заявки изменились — сформируйте заново" : "");
    public bool HasResultStatusError => _resultFeedbackError || ResultIsStale;
    internal bool HasTransientResultFeedback => _resultFeedbackTransient;
    internal long ResultFeedbackVersion => _resultFeedbackVersion;
    public IReadOnlyList<decimal> ResultFontSizes => ResultTextStyle.FontSizes;
    public IReadOnlyList<ResultColorOption> ResultColors { get; } = ResultColorPalette.Create();
    public decimal ResultFontSizePoints
    {
        get => _resultStyle.FontSizePoints;
        set => SaveResultStyle(_resultStyle with { FontSizePoints = value });
    }
    public string ResultTextColor
    {
        get => _resultStyle.ColorHex;
        set => SaveResultStyle(_resultStyle with { ColorHex = value });
    }
    public double ResultFontSizePixels => (double)_resultStyle.FontSizePoints * 96 / 72;
    public IBrush ResultTextBrush => new ImmutableSolidColorBrush(Color.Parse(_resultStyle.ColorHex));
    // The editor's paper colour is for contrast only; it is never copied into the letter.
    public IBrush ResultPaperBrush
    {
        get
        {
            var c = Color.Parse(_resultStyle.ColorHex);
            static double Linear(byte b) => b / 255d <= .04045 ? b / 255d / 12.92 : Math.Pow((b / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B) > .179 ? ResultDarkPaper : ResultLightPaper;
        }
    }
    public string ResultColorDescription => "Цвет текста для письма: " + _resultStyle.ColorHex;
    public ResultTextStyle CaptureResultStyle() => _resultStyle;

    private void ApplyResultStyle(ResultTextStyle? style)
    {
        _resultStyle = (style ?? new()).Normalize();
        foreach (var color in ResultColors) color.IsSelected = color.Hex == _resultStyle.ColorHex;
        foreach (var name in new[] { nameof(ResultFontSizePoints), nameof(ResultFontSizePixels), nameof(ResultTextColor),
            nameof(ResultTextBrush), nameof(ResultPaperBrush), nameof(ResultColorDescription) }) OnPropertyChanged(name);
    }

    private void SaveResultStyle(ResultTextStyle value)
    {
        value = value.Normalize();
        if (!CanEditResult || value == _resultStyle) return;
        try
        {
            var expected = _resultStyle;
            var saved = _preferences.Update(current =>
            {
                var prior = (current.ResultFormatting ?? new()).Normalize();
                if (prior != expected && prior != value) throw new InvalidOperationException("Оформление изменено в другом окне. Обновите настройки перед повторным выбором.");
                return prior == value ? current : current with { ResultFormatting = value };
            });
            _localPreferences = _localPreferences with { ResultFormatting = saved.ResultFormatting };
            ApplyResultStyle(value); SetResultFeedback("");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { SetResultFeedback("Не удалось сохранить оформление. Прежние размер и цвет сохранены.", true); }
        catch (InvalidOperationException ex) { SetResultFeedback(ex.Message, true); }
    }

    internal void SetResultFeedback(string text, bool error = false, bool transient = false)
    {
        _resultFeedback = text; _resultFeedbackError = error;
        _resultFeedbackTransient = transient && !error && text.Length > 0;
        _resultFeedbackVersion++;
        OnPropertyChanged(nameof(ResultFeedbackVersion)); NotifyResultState();
    }

    internal void DismissResultFeedback(long version)
    { if (version == _resultFeedbackVersion && HasTransientResultFeedback) SetResultFeedback(""); }

    private void NotifyResultState()
    {
        foreach (var name in new[] { nameof(IsResultTab), nameof(CanEditResult), nameof(CanCopyResult), nameof(CanClearResult),
            nameof(CanOpenResult), nameof(CanGenerateResult), nameof(OpenResultHint), nameof(GenerateResultHint),
            nameof(CopyResultHint), nameof(ClearResultHint),
            nameof(CanUndoClearResult), nameof(GenerateResultCaption), nameof(ResultStatus), nameof(HasResultStatusError) }) OnPropertyChanged(name);
        OpenResultCommand.NotifyCanExecuteChanged(); GenerateResultCommand.NotifyCanExecuteChanged();
    }
    private void ResultTextEdited(string value)
    {
        _resultTextRevision++; _clearedResult = null;
        if (value.Length > 0) IsResultExpanded = true;
        SetResultFeedback("");
    }
    partial void OnResultIsStaleChanged(bool value) => NotifyResultState();
    private void ResetResultPresentation()
    { _generatedResultText = null; _clearedResult = null; SetResultFeedback(""); }

    internal WorkflowResultGeneration? PrepareResultGeneration()
    {
        if (!CanEditResult || CurrentOrder is not { } order) return null;
        if (ResultGenerationProblem is { } problem) { SetResultFeedback(problem, true); return null; }
        try
        {
            var text = WorkflowResultFormatter.Format(order.ActiveSpecifications.Select(s => s.Current), _workspace.Machines, ResultPricing);
            return new(_store, order.Id, order.Version, ResultText, text,
                ResultText.Length > 0 && ResultText != _generatedResultText && ResultText != text);
        }
        catch (Exception ex) when (ex is WorkflowValidationException or ArgumentException)
        { SetResultFeedback(ex.Message, true); return null; }
    }

    internal bool ApplyResultGeneration(WorkflowResultGeneration draft)
    {
        if (PrepareResultGeneration() is not { } latest) return false;
        if (!ReferenceEquals(draft.Source, latest.Source) || draft.OrderId != latest.OrderId || draft.Version != latest.Version ||
            draft.PreviousText != latest.PreviousText || draft.Text != latest.Text)
        { SetResultFeedback("Данные или текст изменились. Сформируйте результат заново.", true); return false; }
        _generatedResultText = draft.Text; _clearedResult = null; ResultText = draft.Text;
        ResultIsStale = false; OrderTabIndex = 1; SetResultFeedback("Результат сформирован", transient: true); return true;
    }

    [RelayCommand(CanExecute = nameof(CanOpenResult))]
    private void OpenResult()
    {
        if (!CanOpenResult) return;
        OrderTabIndex = 1;
        if (ResultText.Length == 0) GenerateResult();
    }

    [RelayCommand]
    private void ClearResult()
    {
        if (!CanClearResult || CurrentOrder is not { } order) return;
        var text = ResultText; var baseline = _generatedResultText; var stale = ResultIsStale;
        ResultText = ""; ResultIsStale = false;
        _clearedResult = new(_store, order.Id, order.Version, _resultTextRevision, text, baseline, stale);
        SetResultFeedback("Результат очищен", transient: true);
    }

    [RelayCommand]
    private void UndoClearResult()
    {
        if (!CanUndoClearResult || _clearedResult is not { } undo) return;
        _clearedResult = null; ResultText = undo.Text; _generatedResultText = undo.Generated;
        ResultIsStale = undo.Stale || CurrentOrder?.Version != undo.OrderVersion;
        SetResultFeedback("Очистка отменена", transient: true);
    }

    public async Task CopyResultAsync(IResultClipboard clipboard)
    {
        if (!CanCopyResult) return;
        var source = _store; var id = _openOrderId; var text = ResultText; var style = _resultStyle; var revision = _resultTextRevision;
        bool Current() => !_cuttingTaskDetached && ReferenceEquals(source, _store) && _openOrderId == id &&
            revision == _resultTextRevision && ResultText == text && _resultStyle == style;
        _isCopyingResult = true; NotifyCuttingTask(); SetResultFeedback("Копирование…");
        try
        {
            await clipboard.WriteAsync(ResultClipboardContent.FromText(text, style));
            if (Current()) SetResultFeedback("Скопировано", transient: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Current()) SetResultFeedback("Не удалось скопировать. Текст сохранён — повторите копирование.", true);
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Clipboard, ex);
        }
        finally { _isCopyingResult = false; NotifyCuttingTask(); NotifyResultState(); }
    }
}
