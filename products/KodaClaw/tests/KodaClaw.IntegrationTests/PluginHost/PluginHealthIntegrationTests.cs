using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Secrets;
using Xunit;

namespace KodaClaw.IntegrationTests.PluginHost;

public sealed class PluginHealthIntegrationTests
{
    [Fact]
    public async Task Check_health_should_refresh_last_health_for_healthy_plugin()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();
        await fixture.Host.StartAsync(fixture.PluginIdValue);

        var healthy = await fixture.Host.CheckHealthAsync(fixture.PluginIdValue);
        var logs = await fixture.Logs.ListAsync(fixture.PluginIdValue, limit: 20);

        healthy.RuntimeState.Should().Be(PluginRuntimeState.Running);
        healthy.LastHealthAt.Should().NotBeNull();
        healthy.LastError.Should().BeNull();
        logs.Should().Contain(entry =>
            entry.Source == "plugin.health" &&
            entry.Message == "Plugin healthcheck passed.");
    }

    [Fact]
    public async Task Check_health_should_mark_plugin_degraded_after_restart_budget_is_exhausted()
    {
        await using var fixture = await PluginHostFixture.CreateAsync(
            failHealthPing: true,
            configureHost: options => options.MaxRestartAttempts = 1);
        await fixture.Host.StartAsync(fixture.PluginIdValue);

        var degraded = await fixture.Host.CheckHealthAsync(fixture.PluginIdValue);
        var tools = await fixture.Host.GetToolsAsync(fixture.PluginIdValue);
        var logs = await fixture.Logs.ListAsync(fixture.PluginIdValue, limit: 20);
        var diagnostics = fixture.Diagnostics.Query(new DiagnosticsQuery(
            Limit: 20,
            Source: "plugin.health"));

        degraded.RuntimeState.Should().Be(PluginRuntimeState.Degraded);
        degraded.RestartCount.Should().Be(1);
        degraded.LastError.Should().Contain("healthcheck");
        tools.Should().BeEmpty();

        logs.Should().Contain(entry =>
            entry.Source == "plugin.health" &&
            entry.Message.Contains("Plugin healthcheck failed."));
        logs.Should().Contain(entry =>
            entry.Source == "plugin.health" &&
            entry.Message.Contains("returned an error"));

        diagnostics.Should().Contain(entry => entry.EventType == "plugin.degraded");
        diagnostics.Should().Contain(entry => entry.EventType == "plugin.restart.requested");
    }

    [Fact]
    public async Task Check_health_should_resolve_environment_reference_from_secret_store()
    {
        var secretRef = new SecretRef("memory", "plugins", "fixture-health-ping-fail");
        await using var fixture = await PluginHostFixture.CreateAsync(
            configureHost: options => options.MaxRestartAttempts = 1,
            runtimeEnvironmentReferences: new Dictionary<string, string>
            {
                ["KODACLAW_FIXTURE_HEALTH_PING_FAIL"] = secretRef.ToReferenceString(),
            },
            seededSecrets: new Dictionary<SecretRef, string>
            {
                [secretRef] = "1",
            });
        await fixture.Host.StartAsync(fixture.PluginIdValue);

        var degraded = await fixture.Host.CheckHealthAsync(fixture.PluginIdValue);

        degraded.RuntimeState.Should().Be(PluginRuntimeState.Degraded);
        degraded.LastError.Should().Contain("healthcheck");
    }
}
