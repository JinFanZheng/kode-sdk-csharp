using FluentAssertions;
using KodaClaw.ChannelHub.Commands;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// KC-CMD-W1: Registry completeness tests.
/// </summary>
public sealed class ChannelCommandRegistryTests
{
    [Fact]
    public void All_entries_have_at_least_one_alias()
    {
        foreach (var def in ChannelCommandRegistry.All)
        {
            def.Aliases.Should().NotBeEmpty(
                because: $"command '{def.Key}' must have at least one alias");
        }
    }

    [Fact]
    public void All_aliases_start_with_slash()
    {
        foreach (var def in ChannelCommandRegistry.All)
        {
            foreach (var alias in def.Aliases)
            {
                alias.Should().StartWith("/",
                    because: $"alias '{alias}' in command '{def.Key}' must begin with '/'");
            }
        }
    }

    [Fact]
    public void No_duplicate_aliases_across_registry()
    {
        var allAliases = ChannelCommandRegistry.All
            .SelectMany(d => d.Aliases.Select(a => a.ToLowerInvariant()))
            .ToList();

        allAliases.Should().OnlyHaveUniqueItems(because: "each alias must be unique across the registry");
    }

    [Fact]
    public void Each_command_has_either_ControlKind_or_DirectiveKind_but_not_both()
    {
        foreach (var def in ChannelCommandRegistry.All)
        {
            var hasBoth = def.ControlKind.HasValue && def.DirectiveKind.HasValue;
            var hasNeither = !def.ControlKind.HasValue && !def.DirectiveKind.HasValue;

            hasBoth.Should().BeFalse(because: $"'{def.Key}' must not have both ControlKind and DirectiveKind");
            hasNeither.Should().BeFalse(because: $"'{def.Key}' must have exactly one of ControlKind or DirectiveKind");
        }
    }

    [Fact]
    public void Find_returns_null_for_unregistered_alias()
    {
        ChannelCommandRegistry.Find("/unregistered").Should().BeNull();
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/clear")]
    [InlineData("/reset")]
    [InlineData("/status")]
    [InlineData("/s")]
    [InlineData("/stop")]
    [InlineData("/help")]
    [InlineData("/commands")]
    [InlineData("/?")]
    [InlineData("/compact")]
    [InlineData("/tools")]
    [InlineData("/info")]
    [InlineData("/i")]
    [InlineData("/btw")]
    [InlineData("/think")]
    [InlineData("/stream")]
    [InlineData("/quiet")]
    [InlineData("/focus")]
    public void Find_returns_definition_for_all_known_aliases(string alias)
    {
        ChannelCommandRegistry.Find(alias).Should().NotBeNull(
            because: $"'{alias}' is a registered command alias");
    }

    [Theory]
    [InlineData("/NEW")]
    [InlineData("/Status")]
    [InlineData("/THINK")]
    public void Find_is_case_insensitive(string alias)
    {
        ChannelCommandRegistry.Find(alias).Should().NotBeNull(
            because: "alias lookup should be case-insensitive");
    }

    [Fact]
    public void All_control_kinds_are_covered()
    {
        var registeredKinds = ChannelCommandRegistry.All
            .Where(d => d.ControlKind.HasValue)
            .Select(d => d.ControlKind!.Value)
            .ToHashSet();

        foreach (ChannelControlCommandKind kind in Enum.GetValues<ChannelControlCommandKind>())
        {
            registeredKinds.Should().Contain(kind,
                because: $"ChannelControlCommandKind.{kind} must be registered in the registry");
        }
    }

    [Fact]
    public void All_directive_kinds_are_covered()
    {
        var registeredKinds = ChannelCommandRegistry.All
            .Where(d => d.DirectiveKind.HasValue)
            .Select(d => d.DirectiveKind!.Value)
            .ToHashSet();

        foreach (ChannelDirectiveKind kind in Enum.GetValues<ChannelDirectiveKind>())
        {
            // ChannelDirectiveKind.Think is parser-internal fallback:
            // `/think` 的 alias 归属 ThinkToggle control command，非 on/off 参数被 parser 改写为 Think directive。
            // 因此 Think 不需要独立 registry 条目。
            if (kind == ChannelDirectiveKind.Think) continue;

            registeredKinds.Should().Contain(kind,
                because: $"ChannelDirectiveKind.{kind} must be registered in the registry");
        }
    }
}
