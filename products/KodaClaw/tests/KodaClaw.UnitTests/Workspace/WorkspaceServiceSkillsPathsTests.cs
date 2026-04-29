using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

public sealed class WorkspaceServiceSkillsPathsTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspaceService;

    public WorkspaceServiceSkillsPathsTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "kodaclaw-skills-paths-unit",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        var options = new KodaClawWorkspaceOptions { RootPath = _rootPath };
        _workspaceService = new WorkspaceService(options);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore cleanup errors in tests.
        }
    }

    [Fact]
    public void GetSkillsPaths_Returns_Three_Paths()
    {
        var paths = _workspaceService.GetSkillsPaths();

        paths.Should().HaveCount(3);
    }

    [Fact]
    public void GetSkillsPaths_First_Path_Is_AppBase_Skills()
    {
        var paths = _workspaceService.GetSkillsPaths();

        paths[0].Should().EndWith(Path.Combine("skills"));
        paths[0].Should().StartWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void GetSkillsPaths_Second_Path_Is_Global_Agents_Skills()
    {
        var paths = _workspaceService.GetSkillsPaths();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agents",
            "skills");

        paths[1].Should().Be(expected);
    }

    [Fact]
    public void GetSkillsPaths_Last_Path_Is_Workspace_Skills()
    {
        var paths = _workspaceService.GetSkillsPaths();

        paths[2].Should().StartWith(_rootPath);
        paths[2].Should().EndWith(KodaClawWorkspaceLayout.WorkspaceSkillsDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task EnsureInitializedAsync_Creates_WorkspaceSkills_Directory()
    {
        await _workspaceService.EnsureInitializedAsync();

        var expectedDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceSkillsDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(expectedDir).Should().BeTrue("workspace/skills directory should be created on initialization");
    }
}
