using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowWordMapWindowTests
{
    // Deliberately no Application.Hwnd: Word exposes it on a document Window.
    public sealed class Application
    {
        public bool Visible { get; init; }
        public int DisplayAlerts { get; set; }
        public bool QuitCalled { get; private set; }
        public void Quit(int SaveChanges)
        {
            Assert.Equal(-2, SaveChanges); Assert.Equal(-1, DisplayAlerts); QuitCalled = true;
        }
    }
    public sealed class Window { public int Hwnd => 1234; }
    public sealed class Document
    {
        public Window ActiveWindow => new();
        public bool Closed { get; private set; }
        public void Close(int SaveChanges) { Assert.Equal(0, SaveChanges); Closed = true; }
    }
    public sealed class Documents
    {
        public int Count { get; init; }
        public int Opened { get; private set; }
        public Document Probe { get; } = new();
        public Document Open(string FileName, bool ConfirmConversions, bool ReadOnly, bool AddToRecentFiles,
            bool Visible, bool OpenAndRepair, bool NoEncodingDialog)
        {
            Assert.EndsWith(".txt", FileName); Assert.True(ReadOnly); Assert.True(NoEncodingDialog);
            Assert.False(ConfirmConversions); Assert.False(AddToRecentFiles); Assert.False(Visible); Assert.False(OpenAndRepair);
            Opened++; return Probe;
        }
    }

    [Fact]
    public void GetsHandleFromHiddenTextDocumentAndClosesIt()
    {
        var documents = new Documents(); var handle = 0;
        WorkflowWordMapWindow.Probe(new Application(), documents, "probe.txt", o => o, h => handle = h);
        Assert.Equal(1234, handle); Assert.Equal(1, documents.Opened); Assert.True(documents.Probe.Closed);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void ExistingVisibleWindowOrDocumentsAreNotTouched(bool visible, int count)
    {
        var documents = new Documents { Count = count };
        Assert.Throws<WordMapUnavailableException>(() => WorkflowWordMapWindow.Probe(
            new Application { Visible = visible }, documents, "probe.txt", o => o, _ => Assert.Fail("Must not identify a busy instance.")));
        Assert.Equal(0, documents.Opened); Assert.False(documents.Probe.Closed);
    }

    [Fact]
    public void RejectedOwnershipStillClosesOnlyTheProbeDocument()
    {
        var documents = new Documents();
        Assert.Throws<WordMapUnavailableException>(() => WorkflowWordMapWindow.Probe(new Application(), documents,
            "probe.txt", o => o, _ => throw new WordMapUnavailableException("Existing process")));
        Assert.True(documents.Probe.Closed);
    }

    [Fact]
    public void OwnIdleWordQuitsNormallyWithSavePromptEnabled()
    {
        var application = new Application();
        WorkflowWordMapWindow.QuitIfIdle(application, new Documents(), owned: true, closedOwnDocument: true);
        Assert.True(application.QuitCalled);
    }

    [Theory]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, false, 0)]
    [InlineData(true, true, true, 0)]
    [InlineData(true, true, false, 1)]
    [InlineData(true, false, false, 1)]
    public void UserWindowDocumentsOrUncertainOwnershipPreventQuit(bool owned, bool closed, bool visible, int count)
    {
        var application = new Application { Visible = visible };
        WorkflowWordMapWindow.QuitIfIdle(application, new Documents { Count = count }, owned, closed);
        Assert.False(application.QuitCalled); Assert.Equal(0, application.DisplayAlerts);
    }
}
