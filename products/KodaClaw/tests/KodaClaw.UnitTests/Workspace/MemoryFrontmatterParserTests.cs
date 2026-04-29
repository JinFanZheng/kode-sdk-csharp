using FluentAssertions;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Memory;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

public sealed class MemoryFrontmatterParserTests
{
    [Fact]
    public void Parse_ValidFrontmatter_ExtractsAllFields()
    {
        var content = """
            ---
            title: User Preferences
            priority: lasting
            status: active
            created: 2026-03-20
            tags: [personal, settings]
            ---
            Some body content here.
            """;

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Title.Should().Be("User Preferences");
        fm.Priority.Should().Be("lasting");
        fm.Status.Should().Be("active");
        fm.Created.Should().Be("2026-03-20");
        fm.Tags.Should().BeEquivalentTo(["personal", "settings"]);
        fm.BodyStartLine.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_NoFrontmatter_ReturnsDefaults()
    {
        var content = "# Just a heading\n\nSome content.";

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Priority.Should().Be("standard");
        fm.Status.Should().Be("active");
        fm.Title.Should().BeNull();
        fm.Created.Should().BeNull();
        fm.Tags.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsDefaults()
    {
        var fm = MemoryFrontmatterParser.Parse("");

        fm.Priority.Should().Be("standard");
        fm.Status.Should().Be("active");
    }

    [Fact]
    public void Parse_SemanticPriority_ParsesDirectly()
    {
        var content = """
            ---
            priority: permanent
            status: dormant
            ---
            Body text.
            """;

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Priority.Should().Be("permanent");
        fm.Status.Should().Be("dormant");
    }

    [Fact]
    public void Parse_LegacyNumericPriority_NormalizesToSemantic()
    {
        var content = """
            ---
            priority: 0
            ---
            """;

        var fm = MemoryFrontmatterParser.Parse(content);
        fm.Priority.Should().Be("permanent");

        var content3 = """
            ---
            priority: 3
            ---
            """;

        var fm3 = MemoryFrontmatterParser.Parse(content3);
        fm3.Priority.Should().Be("ephemeral");
    }

    [Fact]
    public void Parse_InvalidPriority_DefaultsToStandard()
    {
        var content = """
            ---
            priority: not-a-valid-value
            ---
            """;

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Priority.Should().Be("standard");
    }

    [Fact]
    public void Parse_TagsWithBrackets_ParsesCorrectly()
    {
        var content = """
            ---
            tags: [alpha, beta, gamma]
            ---
            """;

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Tags.Should().BeEquivalentTo(["alpha", "beta", "gamma"]);
    }

    [Fact]
    public void Parse_TagsWithoutBrackets_ParsesCorrectly()
    {
        var content = """
            ---
            tags: foo, bar
            ---
            """;

        var fm = MemoryFrontmatterParser.Parse(content);

        fm.Tags.Should().BeEquivalentTo(["foo", "bar"]);
    }
}
