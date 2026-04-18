using System.Text.Json;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Tools.Builtin.Skills;
using Moq;
using Xunit;

namespace Kode.Agent.Tests.Unit.Skills;

public sealed class SkillListToolTests : IAsyncLifetime
{
    private string _rootDir = "";
    private SkillsManager? _manager;
    private ISandbox? _sandbox;

    public async Task InitializeAsync()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "skill-list-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);

        WriteSkill("code-review",
            description: "Review a pull request for quality, security, and regressions",
            tags: "security, quality");
        WriteSkill("db-migration",
            description: "Plan and execute a relational database schema migration safely",
            tags: "database, operations");
        WriteSkill("style-guide",
            description: "Apply the team C# style guide to a given file or module",
            tags: "style, quality");

        _sandbox = new LocalSandbox(new SandboxOptions { WorkingDirectory = _rootDir, EnforceBoundary = false });
        _manager = new SkillsManager(
            new SkillsConfig { Paths = new[] { _rootDir } },
            _sandbox);
        await _manager.DiscoverAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    private void WriteSkill(string name, string description, string tags)
    {
        var dir = Path.Combine(_rootDir, name);
        Directory.CreateDirectory(dir);
        var content =
            "---\n" +
            $"name: {name}\n" +
            $"description: {description}\n" +
            "metadata:\n" +
            $"  tags: {tags}\n" +
            "---\n" +
            $"# {name}\n\nBody content.\n";
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    private ToolContext BuildContext()
    {
        var agentMock = new Mock<IAgent>();
        agentMock.As<ISkillsAwareAgent>()
            .Setup(a => a.SkillsManager)
            .Returns(_manager);

        return new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = _sandbox!,
            Agent = agentMock.Object
        };
    }

    private static JsonElement Serialize(ToolResult result) =>
        JsonSerializer.SerializeToElement(result.Value);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_NoQuery_ReturnsAllDiscoveredSkills()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(new SkillListArgs(), BuildContext());

        result.Success.Should().BeTrue();
        var json = Serialize(result);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("count").GetInt32().Should().Be(3);
        json.GetProperty("total").GetInt32().Should().Be(3);

        var names = json.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .ToList();
        names.Should().BeEquivalentTo(new[] { "code-review", "db-migration", "style-guide" });
    }

    [Fact]
    public async Task Execute_WithQuery_RanksRelevantSkillFirst()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(new SkillListArgs { Query = "database migration" }, BuildContext());

        var json = Serialize(result);
        var first = json.GetProperty("skills").EnumerateArray().First();
        first.GetProperty("name").GetString().Should().Be("db-migration");
        first.GetProperty("score").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Execute_WithLimit_CapsResultCount()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(new SkillListArgs { Limit = 2 }, BuildContext());

        var json = Serialize(result);
        json.GetProperty("count").GetInt32().Should().Be(2);
        json.GetProperty("total").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Execute_WithTagFilter_ReturnsOnlyMatchingSkills()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(
            new SkillListArgs { Tags = new[] { "quality" } },
            BuildContext());

        var json = Serialize(result);
        var names = json.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .ToList();
        names.Should().BeEquivalentTo(new[] { "code-review", "style-guide" });
    }

    [Fact]
    public async Task Execute_WithTagFilterAndQuery_RanksWithinFilteredSet()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(
            new SkillListArgs { Tags = new[] { "quality" }, Query = "style guide" },
            BuildContext());

        var json = Serialize(result);
        var names = json.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("style-guide");
        names.Should().NotContain("db-migration"); // filtered out by tag
        names.First().Should().Be("style-guide"); // top-ranked within filter
    }

    [Fact]
    public async Task Execute_WithUnmatchedTag_ReturnsEmptyResult()
    {
        var tool = new SkillListTool();
        var result = await tool.ExecuteAsync(
            new SkillListArgs { Tags = new[] { "nonexistent" } },
            BuildContext());

        var json = Serialize(result);
        json.GetProperty("count").GetInt32().Should().Be(0);
    }

}
