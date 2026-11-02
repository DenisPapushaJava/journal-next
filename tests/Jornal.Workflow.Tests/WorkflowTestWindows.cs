using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

internal static class WorkflowTestWindows
{
    internal static async Task AcknowledgeShopProgressAsync(Window owner)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var progress = owner.OwnedWindows.OfType<WorkflowShopProgressWindow>().SingleOrDefault();
            if (progress?.FindControl<Button>("ShopProgressClose") is { IsVisible: true } close)
            {
                Assert.Equal("ОК", close.Content);
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Assert.False(progress.IsVisible); return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Completed transfer did not leave its result window open.");
    }
    // Explicitly discard test input during cleanup, exercising the real confirmation path.
    internal static void Close(Window window)
    {
        if (window is not WorkflowWindow) { window.Close(); return; }
        foreach (var child in window.OwnedWindows.ToArray()) child.Close();
        window.Close();
        for (var i = 0; i < 500 && window.IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (window.DataContext is WorkflowViewModel { IsBusy: false })
                foreach (var progress in window.OwnedWindows.OfType<WorkflowShopProgressWindow>().ToArray()) progress.Finish(forceClose: true);
            if (i % 10 == 0 && window.OwnedWindows.Count == 0) window.Close();
            foreach (var dialog in window.OwnedWindows.OfType<WorkflowCloseWindow>().ToArray())
                dialog.FindControl<Button>("DiscardWorkflowClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            if (window.IsVisible) Thread.Sleep(1);
        }
        Assert.False(window.IsVisible, "Test window did not close; finish its active operation before cleanup.");
    }
}
