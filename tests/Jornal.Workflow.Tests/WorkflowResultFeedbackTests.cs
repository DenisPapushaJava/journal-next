using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowResultFeedbackTests
{
    private sealed class Clipboard(Func<Task> write) : IResultClipboard
    { public Task WriteAsync(ResultClipboardContent content) => write(); }

    [AvaloniaFact]
    public async Task OnlySuccessExpiresAndQueuedCallbacksCannotDismissNewerFeedback()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var clock = new MapNoticeClock();
        var window = new WorkflowWindow(new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")), resultFeedbackClock: clock) { DataContext = f.Model };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.GenerateResultCommand.Execute(null); Pump();
            var geometry = Geometry(window); var first = clock.Timers.Last();
            clock.Advance(2); Pump(); Assert.Equal("Результат сформирован", f.Model.ResultStatus);
            f.Model.GenerateResultCommand.Execute(null); Pump(); first.Fire(); Pump();
            Assert.Equal("Результат сформирован", f.Model.ResultStatus);
            clock.Advance(2); Pump(); Assert.NotEmpty(f.Model.ResultStatus);
            clock.Advance(1); Pump(); Assert.Empty(f.Model.ResultStatus); Assert.Equal(geometry, Geometry(window));

            var copyFinished = new TaskCompletionSource();
            var copy = f.Model.CopyResultAsync(new Clipboard(() => copyFinished.Task));
            clock.Advance(30); Pump(); Assert.Equal("Копирование…", f.Model.ResultStatus);
            copyFinished.SetResult(); await copy; Pump(); Assert.Equal("Скопировано", f.Model.ResultStatus);
            var copied = clock.Timers.Last();
            await f.Model.CopyResultAsync(new Clipboard(() => throw new IOException("test"))); Pump();
            copied.Fire(); clock.Advance(30); Pump(); Assert.True(f.Model.HasResultStatusError);
            Assert.Contains("Не удалось скопировать", f.Model.ResultStatus);

            f.Model.ClearResultCommand.Execute(null); Pump(); Assert.Equal("Результат очищен", f.Model.ResultStatus);
            clock.Advance(3); Pump(); Assert.Empty(f.Model.ResultStatus); Assert.True(f.Model.CanUndoClearResult);
            f.Model.UndoClearResultCommand.Execute(null); Pump(); Assert.Equal("Очистка отменена", f.Model.ResultStatus);
            f.Model.ResultIsStale = true; clock.Advance(3); Pump();
            Assert.Equal("Данные заявки изменились — сформируйте заново", f.Model.ResultStatus);
            Assert.True(f.Model.HasResultStatusError); Assert.Equal(geometry, Geometry(window));

            f.Model.GenerateResultCommand.Execute(null); Pump(); var detached = clock.Timers.Last();
            window.DataContext = null; Pump(); Assert.True(detached.Disposed); detached.Fire(); Pump();
            Assert.Equal("Результат сформирован", f.Model.ResultStatus);
            window.DataContext = f.Model; Pump(); var closed = clock.Timers.Last();
            window.Close(); Assert.All(clock.Timers, timer => Assert.True(timer.Disposed)); closed.Fire(); Pump();
            Assert.Equal("Результат сформирован", f.Model.ResultStatus);
        }
        finally { window.Close(); }
    }

    private static Rect[] Geometry(Window w) => new[] { "WorkflowResultEditor", "WorkflowResultWorkspace", "WorkflowOrderTotal" }
        .Select(n => w.FindControl<Control>(n)!).Select(c => new Rect(c.TranslatePoint(default, w)!.Value, c.Bounds.Size)).ToArray();
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 500; i++) { Pump(); if (condition()) return; await Task.Delay(10, TestContext.Current.CancellationToken); } Assert.True(condition()); }
}
