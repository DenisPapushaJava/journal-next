using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Kept separate from native process handling so the Word object-model contract can be tested.
internal static class WorkflowWordMapWindow
{
    internal static void QuitIfIdle(object application, object collection, bool owned, bool closedOwnDocument)
    {
        if (!owned || !closedOwnDocument) return;
        dynamic word = application; dynamic documents = collection;
        if ((bool)word.Visible || (int)documents.Count != 0) return;
        // A user could open a document after the check. Keep Word's save prompt enabled.
        word.DisplayAlerts = -1; // wdAlertsAll
        word.Quit(SaveChanges: -2); // wdPromptToSaveChanges
    }

    internal static void Probe(object application, object collection, string path, Func<object, object> keep, Action<int> identify)
    {
        dynamic word = application; dynamic documents = collection;
        if ((bool)word.Visible || (int)documents.Count != 0)
            throw new WordMapUnavailableException("Word используется другим окном. DOC-карта не изменена.");
        dynamic? probe = null;
        try
        {
            // Plain text cannot contain document macros, links or an attached template.
            probe = keep(documents.Open(FileName: path, ConfirmConversions: false, ReadOnly: true,
                AddToRecentFiles: false, Visible: false, OpenAndRepair: false, NoEncodingDialog: true));
            dynamic window = keep(probe.ActiveWindow);
            identify((int)window.Hwnd);
        }
        finally
        {
            if (probe is not null) probe.Close(SaveChanges: 0);
        }
    }
}
