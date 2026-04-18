using FluentAssertions;
using Kode.Agent.Sdk.Core.Skills;
using Xunit;

namespace Kode.Agent.Tests.Unit.Skills;

public sealed class SkillsInjectorTests
{
    private static SkillMetadata Meta(string name, string description) => new()
    {
        Name = name,
        Description = description
    };

    [Fact]
    public void ToPromptXml_Empty_returns_empty_string()
    {
        SkillsInjector.ToPromptXml(Array.Empty<SkillMetadata>())
            .Should().BeEmpty();
    }

    [Fact]
    public void ToPromptXml_FullMode_includes_name_and_description()
    {
        var xml = SkillsInjector.ToPromptXml(
            new[] { Meta("code-review", "Review a pull request for quality and regressions") });

        xml.Should().Contain("<name>code-review</name>");
        xml.Should().Contain("<description>Review a pull request for quality and regressions</description>");
        xml.Should().Contain("skill_activate");
    }

    [Fact]
    public void ToPromptXml_NamesOnly_omits_description()
    {
        var xml = SkillsInjector.ToPromptXml(
            new[] { Meta("code-review", "Review a pull request for quality and regressions") },
            SkillsInjectionMode.NamesOnly);

        xml.Should().Contain("<name>code-review</name>");
        xml.Should().NotContain("<description>");
        xml.Should().Contain("skill_list");
    }

    [Fact]
    public void ToPromptXml_None_returns_empty_string()
    {
        var xml = SkillsInjector.ToPromptXml(
            new[] { Meta("code-review", "desc") },
            SkillsInjectionMode.None);

        xml.Should().BeEmpty();
    }
}
