using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using Xunit;

namespace KodaClaw.IntegrationTests.PluginHost;

public sealed class PluginLifecycleHostIntegrationTests
{
    [Fact]
    public async Task Start_should_connect_stdio_plugin_and_load_namespaced_tools()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();

        var record = await fixture.Host.StartAsync(fixture.PluginIdValue);
        var tools = await fixture.Host.GetToolsAsync(fixture.PluginIdValue);
        var reloaded = await fixture.ReloadAsync();

        record.RuntimeState.Should().Be(PluginRuntimeState.Running);
        record.LastStartedAt.Should().NotBeNull();
        tools.Select(tool => tool.Name).Should().Equal("mcp__plugin.fixture__echo");

        reloaded.RuntimeState.Should().Be(PluginRuntimeState.Running);
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Stop_and_restart_should_persist_runtime_state_transitions()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();

        await fixture.Host.StartAsync(fixture.PluginIdValue);
        var stopped = await fixture.Host.StopAsync(fixture.PluginIdValue);
        var restarted = await fixture.Host.RestartAsync(fixture.PluginIdValue);
        var logs = await fixture.Logs.ListAsync(fixture.PluginIdValue, limit: 20);

        stopped.RuntimeState.Should().Be(PluginRuntimeState.Stopped);
        stopped.LastStoppedAt.Should().NotBeNull();

        restarted.RuntimeState.Should().Be(PluginRuntimeState.Running);
        restarted.RestartCount.Should().Be(1);
        restarted.LastStartedAt.Should().NotBeNull();

        logs.Select(entry => entry.Message).Should().Contain([
            "Plugin stopped.",
            "Plugin restart requested.",
            "Plugin restarted successfully.",
        ]);
    }
}
