using FluentAssertions;
using KodaClaw.Gateway;
using Xunit;

namespace KodaClaw.UnitTests.Gateway;

public sealed class SkillFrontmatterParserTests
{
    // ─── Parse: standard format (metadata block + allowed-tools) ──────────

    [Fact]
    public void Parse_FullStandardFrontmatter_AllFieldsExtracted()
    {
        const string content = """
            ---
            name: koda-automation
            description: HEARTBEAT.md 自动化规则编写指南
            license: built-in
            compatibility: KodaClaw 1.x
            allowed-tools: workspace_protocol_update workspace_read
            metadata:
              kind: builtin-core
              version: "1.0"
              tags: "automation, heartbeat, scheduling"
            ---

            # Skill content here
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.Description.Should().Be("HEARTBEAT.md 自动化规则编写指南");
        result.Kind.Should().Be("builtin-core");
        result.Version.Should().Be("1.0");
        result.Tags.Should().BeEquivalentTo(["automation", "heartbeat", "scheduling"]);
        result.AllowedTools.Should().BeEquivalentTo(["workspace_protocol_update", "workspace_read"]);
        result.Compatibility.Should().Be("KodaClaw 1.x");
    }

    [Fact]
    public void Parse_AllowedTools_SpaceDelimited()
    {
        const string content = """
            ---
            name: my-skill
            description: A skill
            allowed-tools: tool_a tool_b tool_c
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.AllowedTools.Should().BeEquivalentTo(["tool_a", "tool_b", "tool_c"]);
    }

    [Fact]
    public void Parse_AllowedTools_SingleTool()
    {
        const string content = """
            ---
            name: my-skill
            description: A skill
            allowed-tools: channel_send
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.AllowedTools.Should().BeEquivalentTo(["channel_send"]);
    }

    [Fact]
    public void Parse_MetadataBlock_KindVersionTagsExtracted()
    {
        const string content = """
            ---
            name: koda-workspace
            description: Workspace guide
            metadata:
              kind: builtin-core
              version: "2.0"
              tags: "workspace, protocol"
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.Kind.Should().Be("builtin-core");
        result.Version.Should().Be("2.0");
        result.Tags.Should().BeEquivalentTo(["workspace", "protocol"]);
    }

    [Fact]
    public void Parse_MetadataBlock_TagsCommaSplit()
    {
        const string content = """
            ---
            name: my-skill
            description: A skill
            metadata:
              tags: "alpha, beta, gamma"
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.Tags.Should().BeEquivalentTo(["alpha", "beta", "gamma"]);
    }

    // ─── Parse: missing optional fields default correctly ──────────────────

    [Fact]
    public void Parse_MissingKind_DefaultsToOptional()
    {
        const string content = """
            ---
            name: my-skill
            description: A skill
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.Kind.Should().Be("optional");
    }

    [Fact]
    public void Parse_MissingAllowedTools_ReturnsEmptyList()
    {
        const string content = "---\nname: my-skill\n---";

        SkillFrontmatterParser.Parse(content).AllowedTools.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MissingTags_ReturnsEmptyList()
    {
        const string content = "---\nname: my-skill\n---";

        SkillFrontmatterParser.Parse(content).Tags.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MissingVersion_ReturnsNull()
    {
        const string content = "---\nname: my-skill\n---";

        SkillFrontmatterParser.Parse(content).Version.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingCompatibility_ReturnsNull()
    {
        const string content = "---\nname: my-skill\n---";

        SkillFrontmatterParser.Parse(content).Compatibility.Should().BeNull();
    }

    // ─── Parse: no frontmatter ──────────────────────────────────────────────

    [Fact]
    public void Parse_NoFrontmatter_AllDefaultValues()
    {
        const string content = "# Just content\nNo YAML here";

        var result = SkillFrontmatterParser.Parse(content);

        result.Description.Should().BeNull();
        result.Kind.Should().Be("optional");
        result.Version.Should().BeNull();
        result.Tags.Should().BeEmpty();
        result.AllowedTools.Should().BeEmpty();
        result.Compatibility.Should().BeNull();
    }

    // ─── Parse: content after frontmatter is ignored ────────────────────────

    [Fact]
    public void Parse_DescriptionInContentSection_Ignored()
    {
        const string content = """
            ---
            name: my-skill
            ---
            description: this should NOT be parsed
            """;

        SkillFrontmatterParser.Parse(content).Description.Should().BeNull();
    }

    // ─── Parse: multi-line description via YAML block scalars ──────────────

    [Fact]
    public void Parse_Description_FoldedScalar_JoinsLines()
    {
        const string content = """
            ---
            name: my-skill
            description: >
              Use this skill whenever the user wants to
              do some multi-step task that needs a long
              trigger description.
            ---
            """;

        SkillFrontmatterParser.Parse(content).Description
            .Should().Be("Use this skill whenever the user wants to do some multi-step task that needs a long trigger description.");
    }

    [Fact]
    public void Parse_Description_LiteralScalar_PreservesNewlines()
    {
        const string content = """
            ---
            name: my-skill
            description: |
              Line A
              Line B
            ---
            """;

        SkillFrontmatterParser.Parse(content).Description
            .Should().Be("Line A\nLine B");
    }

    [Fact]
    public void Parse_Description_FoldedScalar_DoesNotEatSiblingFields()
    {
        const string content = """
            ---
            name: my-skill
            description: >
              multi-line
              desc
            compatibility: KodaClaw 1.x
            metadata:
              kind: optional
              version: "1.0"
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        result.Description.Should().Be("multi-line desc");
        result.Compatibility.Should().Be("KodaClaw 1.x");
        result.Kind.Should().Be("optional");
        result.Version.Should().Be("1.0");
    }
}
