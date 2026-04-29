using FluentAssertions;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Memory;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

public sealed class MemoryMarkdownParserTests
{
    // ── ParseSections ────────────────────────────────────────────────────────

    [Fact]
    public void ParseSections_EmptyContent_ReturnsEmpty()
    {
        var sections = MemoryMarkdownParser.ParseSections("");

        sections.Should().BeEmpty();
    }

    [Fact]
    public void ParseSections_SingleSection_ReturnsOne()
    {
        var content = """
            ## User Preferences
            - Prefers dark mode
            - Language: zh-CN
            """;

        var sections = MemoryMarkdownParser.ParseSections(content);

        sections.Should().HaveCount(1);
        sections[0].Title.Should().Be("User Preferences");
        sections[0].Body.Should().Contain("Prefers dark mode");
    }

    [Fact]
    public void ParseSections_MultipleSections_ReturnsAll()
    {
        var content = """
            ## User Preferences
            - Prefers dark mode

            ## Project Context
            Working on KodaClaw

            ## Technical Notes
            Uses .NET 10
            """;

        var sections = MemoryMarkdownParser.ParseSections(content);

        sections.Should().HaveCount(3);
        sections[0].Title.Should().Be("User Preferences");
        sections[1].Title.Should().Be("Project Context");
        sections[2].Title.Should().Be("Technical Notes");
    }

    // ── NormalizeKey ─────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeKey_BasicTitle_ReturnsLowercaseHyphenated()
    {
        var key = MemoryMarkdownParser.NormalizeKey("User Preferences");

        key.Should().Be("user-preferences");
    }

    [Fact]
    public void NormalizeKey_ChineseTitle_PreservesCjk()
    {
        var key = MemoryMarkdownParser.NormalizeKey("用户偏好设置");

        key.Should().Be("用户偏好设置");
    }

    [Fact]
    public void NormalizeKey_SpecialChars_Stripped()
    {
        var key = MemoryMarkdownParser.NormalizeKey("User's Notes & Tips!");

        key.Should().NotContain("'");
        key.Should().NotContain("&");
        key.Should().NotContain("!");
        key.Should().Contain("user");
        key.Should().Contain("notes");
        key.Should().Contain("tips");
    }
}
