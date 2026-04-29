using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class BootstrapServiceContractTests
{
    [Fact]
    public async Task Complete_should_write_identity_soul_user_update_config_and_archive_bootstrap()
    {
        using var tempDir = new TempDir();
        var workspaceService = CreateWorkspaceService(tempDir.Path);
        var bootstrapService = new BootstrapService(workspaceService);

        var identityMarkdown = "# identity";
        var soulMarkdown = "# soul";
        var userMarkdown = "# user";

        var result = await bootstrapService.CompleteAsync(
            new BootstrapCompletionRequest(identityMarkdown, soulMarkdown, userMarkdown));

        result.WorkspaceRootPath.Should().Be(tempDir.Path);
        result.BootstrapCompleted.Should().BeTrue();
        result.BootstrapFileArchived.Should().BeTrue();

        File.ReadAllText(result.IdentityFilePath).Should().Be(identityMarkdown);
        File.ReadAllText(result.SoulFilePath).Should().Be(soulMarkdown);
        File.ReadAllText(result.UserFilePath).Should().Be(userMarkdown);

        var bootstrapPath = Path.Combine(
            tempDir.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.BootstrapFile);
        File.Exists(bootstrapPath).Should().BeFalse();
        File.Exists(bootstrapPath + ".archived").Should().BeTrue();

        var appConfig = await workspaceService.LoadAppConfigAsync();
        appConfig.BootstrapCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_should_throw_when_identity_markdown_missing()
    {
        using var tempDir = new TempDir();
        var workspaceService = CreateWorkspaceService(tempDir.Path);
        var bootstrapService = new BootstrapService(workspaceService);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            bootstrapService.CompleteAsync(new BootstrapCompletionRequest(null!, "# soul", "# user")));
    }

    [Fact]
    public async Task Complete_should_throw_when_soul_markdown_missing()
    {
        using var tempDir = new TempDir();
        var workspaceService = CreateWorkspaceService(tempDir.Path);
        var bootstrapService = new BootstrapService(workspaceService);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            bootstrapService.CompleteAsync(new BootstrapCompletionRequest("# identity", null!, "# user")));
    }

    [Fact]
    public async Task Complete_should_delete_bootstrap_when_archive_disabled()
    {
        using var tempDir = new TempDir();
        var workspaceService = CreateWorkspaceService(tempDir.Path);
        var bootstrapService = new BootstrapService(workspaceService);

        var result = await bootstrapService.CompleteAsync(
            new BootstrapCompletionRequest("# identity", "# soul", "# user", ArchiveBootstrapFile: false));

        var bootstrapPath = Path.Combine(
            tempDir.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.BootstrapFile);

        result.BootstrapFileArchived.Should().BeFalse();
        File.Exists(bootstrapPath).Should().BeFalse();
        File.Exists(bootstrapPath + ".archived").Should().BeFalse();
    }

    private static WorkspaceService CreateWorkspaceService(string rootPath)
    {
        return new WorkspaceService(new KodaClawWorkspaceOptions
        {
            RootPath = rootPath,
        });
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kodaclaw-bootstrap-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
