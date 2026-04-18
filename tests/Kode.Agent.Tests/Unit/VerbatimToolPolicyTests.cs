using FluentAssertions;
using Kode.Agent.Sdk.Core.Agent;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class VerbatimToolPolicyTests
{
    [Theory]
    [InlineData("fs_read")]
    [InlineData("fs_grep")]
    [InlineData("fs_list")]
    [InlineData("fs_glob")]
    [InlineData("fs_edit")]
    [InlineData("fs_write")]
    public void IsVerbatim_True_ForBuiltinFileTools(string toolName)
    {
        VerbatimToolPolicy.IsVerbatim(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("bash_run")]
    [InlineData("mcp__foo__bar")]
    [InlineData("custom_tool")]
    public void IsVerbatim_False_ForCompressibleTools(string toolName)
    {
        VerbatimToolPolicy.IsVerbatim(toolName).Should().BeFalse();
    }

    [Fact]
    public void IsVerbatim_CaseInsensitive()
    {
        VerbatimToolPolicy.IsVerbatim("FS_READ").Should().BeTrue();
        VerbatimToolPolicy.IsVerbatim("Fs_Grep").Should().BeTrue();
    }

    [Fact]
    public void IsVerbatim_HostOverride_Augments_Defaults()
    {
        var extra = new[] { "my_sensitive_tool" };

        VerbatimToolPolicy.IsVerbatim("my_sensitive_tool", extra).Should().BeTrue();
        VerbatimToolPolicy.IsVerbatim("fs_read", extra).Should().BeTrue();            // defaults still active
        VerbatimToolPolicy.IsVerbatim("other_tool", extra).Should().BeFalse();
    }

    [Fact]
    public void IsVerbatim_EmptyOrNullName_ReturnsFalse()
    {
        VerbatimToolPolicy.IsVerbatim("").Should().BeFalse();
        VerbatimToolPolicy.IsVerbatim(null!).Should().BeFalse();
    }
}
