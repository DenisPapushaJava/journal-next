using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOfficePilotPathTests
{
    [Fact]
    public void OnlyTheDesignatedFileInAProjectUnderTheRootIsExposed()
    {
        var fixture = Directory.CreateTempSubdirectory();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture.FullName, "root")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "Раскрой")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(fixture.FullName, "root-copy")).FullName;
            var native = Path.Combine(project, "Раскрой.dsp");
            var pilot = Path.Combine(project, WorkflowOfficePilotPath.FileName);
            File.WriteAllText(native, "CAM file");
            Assert.Null(WorkflowOfficePilotPath.Find(root, native));
            File.WriteAllText(pilot, "Test file");
            File.WriteAllText(Path.Combine(root, WorkflowOfficePilotPath.FileName), "Root file");
            File.WriteAllText(Path.Combine(outside, WorkflowOfficePilotPath.FileName), "Outside file");
            Assert.Equal(pilot, WorkflowOfficePilotPath.Find(root, native));
            Assert.Null(WorkflowOfficePilotPath.Find(root, Path.Combine(root, "root.dsp")));
            Assert.Null(WorkflowOfficePilotPath.Find(root, Path.Combine(outside, "outside.dsp")));
            Assert.Null(WorkflowOfficePilotPath.Find(root, Path.Combine(project, "..", "..", "root-copy", "outside.dsp")));
        }
        finally { fixture.Delete(recursive: true); }
    }
}
