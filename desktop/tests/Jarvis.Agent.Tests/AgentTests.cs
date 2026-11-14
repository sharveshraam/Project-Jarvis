using Jarvis.Agent;
using Jarvis.Core.Search;

namespace Jarvis.Agent.Tests;

public class WorkflowTests
{
    [Fact]
    public void PlanFileOrganisation_ListsCategories_WithoutMoving()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.pdf"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "b.png"), "y");
            var planner = new WorkflowPlanner(null!);
            var plan = planner.PlanFileOrganisation(dir.FullName);
            Assert.Contains(plan, p => p.Contains("Documents"));
            Assert.Contains(plan, p => p.Contains("Images"));
            // Nothing moved: files still in place.
            Assert.True(File.Exists(Path.Combine(dir.FullName, "a.pdf")));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void SafeFileOps_Move_NeverOverwrites()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var src = Path.Combine(dir.FullName, "src.txt");
            var dst = Path.Combine(dir.FullName, "dst.txt");
            File.WriteAllText(src, "new");
            File.WriteAllText(dst, "existing");
            var result = SafeFileOps.MoveFile(src, dst);
            Assert.Contains("dst (1).txt", result);
            Assert.Equal("existing", File.ReadAllText(dst));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dir.FullName, "dst (1).txt")));
        }
        finally { dir.Delete(recursive: true); }
    }
}
