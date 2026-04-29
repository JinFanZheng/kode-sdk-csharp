using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Skills;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class SkillsEndpointIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_skills_should_require_token()
    {
        using var workspace = new SkillsTempWorkspace();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/skills");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_skills_returns_workspace_skill_with_new_frontmatter_fields()
    {
        using var workspace = new SkillsTempWorkspace();
        await SeedWorkspaceAsync(workspace.Path);
        WriteSkillFile(workspace.Path, "koda-test", """
            ---
            name: koda-test
            description: Test skill
            license: built-in
            compatibility: KodaClaw 1.x
            allowed-tools: workspace_read workspace_protocol_update
            metadata:
              kind: builtin-core
              version: "2.0"
              tags: "test, ci"
            ---
            # Test skill content
            """);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/skills");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<SkillDescriptor[]>(JsonOptions);
        items.Should().NotBeNull();
        var skill = items!.Should().ContainSingle(s => s.Name == "koda-test").Subject;
        skill.Description.Should().Be("Test skill");
        skill.Kind.Should().Be("builtin-core");
        skill.Version.Should().Be("2.0");
        skill.Tags.Should().Equal("test", "ci");
        skill.AllowedTools.Should().Equal("workspace_read", "workspace_protocol_update");
        skill.Compatibility.Should().Be("KodaClaw 1.x");
        skill.Source.Should().Be("workspace");
    }

    [Fact]
    public async Task Get_skills_missing_kind_defaults_to_optional()
    {
        using var workspace = new SkillsTempWorkspace();
        await SeedWorkspaceAsync(workspace.Path);
        WriteSkillFile(workspace.Path, "no-kind-skill", """
            ---
            name: no-kind-skill
            description: A skill without kind
            ---
            # Content
            """);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/skills");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<SkillDescriptor[]>(JsonOptions);
        var skill = items!.Should().ContainSingle(s => s.Name == "no-kind-skill").Subject;
        skill.Kind.Should().Be("optional");
        skill.Tags.Should().BeEmpty();
        skill.AllowedTools.Should().BeEmpty();
        skill.Version.Should().BeNull();
        skill.Compatibility.Should().BeNull();
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task SeedWorkspaceAsync(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        using var provider = services.BuildServiceProvider();
        var ws = provider.GetRequiredService<IWorkspaceService>();
        await ws.EnsureInitializedAsync();
        await ws.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
        });
    }

    private static void WriteSkillFile(string workspaceRoot, string skillName, string content)
    {
        var skillDir = Path.Combine(workspaceRoot, "workspace", "skills", skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), content.Trim());
    }

    private sealed class SkillsTempWorkspace : IDisposable
    {
        public SkillsTempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-skills-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
