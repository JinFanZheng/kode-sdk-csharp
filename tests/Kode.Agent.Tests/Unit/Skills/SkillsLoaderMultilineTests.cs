using FluentAssertions;
using Kode.Agent.Sdk.Core.Skills;
using Xunit;

namespace Kode.Agent.Tests.Unit.Skills;

/// <summary>
/// Covers YAML block scalar handling (<c>|</c> literal and <c>&gt;</c> folded, with
/// optional chomping modifiers) in <see cref="SkillsLoader.ParseFrontmatter"/>.
/// </summary>
public sealed class SkillsLoaderMultilineTests
{
    [Fact]
    public void Folded_scalar_joins_lines_with_space()
    {
        const string content = """
            ---
            name: demo
            description: >
              Use this skill when the user wants to
              perform some long-running task
              that spans multiple sentences.
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be(
            "Use this skill when the user wants to perform some long-running task that spans multiple sentences.");
    }

    [Fact]
    public void Folded_scalar_blank_line_becomes_newline()
    {
        const string content = """
            ---
            name: demo
            description: >
              First paragraph stays together
              across two source lines.

              Second paragraph after a blank line.
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be(
            "First paragraph stays together across two source lines.\nSecond paragraph after a blank line.");
    }

    [Fact]
    public void Literal_scalar_preserves_line_breaks()
    {
        const string content = """
            ---
            name: demo
            description: |
              Line one.
              Line two.
              Line three.
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be("Line one.\nLine two.\nLine three.");
    }

    [Fact]
    public void Literal_scalar_strip_chomping_drops_trailing_newlines()
    {
        const string content = """
            ---
            name: demo
            description: |-
              One
              Two
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be("One\nTwo");
    }

    [Fact]
    public void Folded_scalar_strip_chomping_trims_trailing()
    {
        const string content = """
            ---
            name: demo
            description: >-
              Word one
              word two
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be("Word one word two");
    }

    [Fact]
    public void Block_scalar_ends_before_next_top_level_key()
    {
        const string content = """
            ---
            name: demo
            description: |
              keep this
              and this
            license: Apache-2.0
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be("keep this\nand this");
        meta.License.Should().Be("Apache-2.0");
    }

    [Fact]
    public void Block_scalar_does_not_swallow_trailing_blank_line()
    {
        const string content = """
            ---
            name: demo
            description: |
              only line

            license: MIT
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().Be("only line");
        meta.License.Should().Be("MIT");
    }

    [Fact]
    public void Compatibility_field_supports_multi_line()
    {
        const string content = """
            ---
            name: demo
            description: d
            compatibility: >
              Requires claude-sonnet-4 or newer;
              OpenAI gpt-4o also works.
            ---
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Compatibility.Should().Be(
            "Requires claude-sonnet-4 or newer; OpenAI gpt-4o also works.");
    }

    [Fact]
    public void ParseFrontmatter_does_not_fallback_to_body_when_description_missing()
    {
        const string content = """
            ---
            name: demo
            ---

            # Heading
            This body line should NOT become the description.
            """;

        var meta = SkillsLoader.ParseFrontmatter(content);

        meta.Description.Should().BeEmpty();
    }

    // ─── Spec-deviation warnings ─────────────────────────────────────────────

    [Fact]
    public void Warns_when_allowed_tools_is_inside_metadata_block()
    {
        const string content = """
            ---
            name: demo
            description: d
            metadata:
              allowed-tools: fs_read fs_write
            ---
            """;

        _ = SkillsLoader.ParseFrontmatter(content, out var warnings);

        warnings.Should().ContainSingle(w => w.Contains("allowed-tools") && w.Contains("top-level"));
    }

    [Fact]
    public void Warns_when_compatibility_is_inside_metadata_block()
    {
        const string content = """
            ---
            name: demo
            description: d
            metadata:
              compatibility: KodaClaw 1.x
            ---
            """;

        _ = SkillsLoader.ParseFrontmatter(content, out var warnings);

        warnings.Should().ContainSingle(w => w.Contains("compatibility") && w.Contains("top-level"));
    }

    [Fact]
    public void Warns_when_metadata_value_is_flow_sequence()
    {
        const string content = """
            ---
            name: demo
            description: d
            metadata:
              tags: [alpha, beta]
            ---
            """;

        _ = SkillsLoader.ParseFrontmatter(content, out var warnings);

        warnings.Should().ContainSingle(w => w.Contains("flow sequence") && w.Contains("tags"));
    }

    [Fact]
    public void No_warnings_for_spec_compliant_frontmatter()
    {
        const string content = """
            ---
            name: demo
            description: d
            compatibility: KodaClaw 1.x
            allowed-tools: fs_read fs_write
            metadata:
              kind: builtin-core
              version: "1.0"
              tags: "a, b, c"
            ---
            """;

        _ = SkillsLoader.ParseFrontmatter(content, out var warnings);

        warnings.Should().BeEmpty();
    }
}
