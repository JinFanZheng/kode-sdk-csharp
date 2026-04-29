using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using Xunit;

namespace KodaClaw.UnitTests.Contracts;

public sealed class WorkspaceContractsTests
{
    [Fact]
    public void Workspace_app_config_should_default_to_current_workspace_version()
    {
        var config = new WorkspaceAppConfig();

        config.WorkspaceVersion.Should().Be(KodaClawWorkspaceLayout.CurrentWorkspaceVersion);
        config.BootstrapCompleted.Should().BeFalse();
        config.ActiveMainSessionId.Should().BeNull();
    }

    [Fact]
    public void Workspace_layout_should_expose_stable_root_directory_name()
    {
        KodaClawWorkspaceLayout.RootDirectoryName.Should().Be(".kodaclaw");
        KodaClawWorkspaceLayout.BootstrapFile.Should().Be("BOOTSTRAP.md");
        KodaClawWorkspaceLayout.AppConfigFile.Should().Be("app.json");
    }
}
