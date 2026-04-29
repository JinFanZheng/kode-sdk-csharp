using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

public sealed class ChannelThreadSummaryIntegrationTests
{
    [Fact]
    public async Task Summary_writer_should_be_registered_and_create_file()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-summary-integration");
        using var provider = CreateServiceProvider(workspace.Path);

        var writer = provider.GetRequiredService<IChannelThreadSummaryWriter>();
        var binding = BuildBinding("binding-summary-001");
        var outcome = new ChannelTurnOutcome(
            Kind: ChannelTurnOutcomeKind.Delivered,
            Summary: "Hello from integration test.",
            OccurredAt: new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero));

        await writer.WriteAsync(binding, outcome);

        var expectedFile = Path.Combine(
            workspace.Path, "workspace", "channels", "binding-summary-001", "SUMMARY.md");
        File.Exists(expectedFile).Should().BeTrue();
        var content = await File.ReadAllTextAsync(expectedFile);
        content.Should().Contain("delivered");
        content.Should().Contain("Hello from integration test.");
    }

    [Fact]
    public async Task Summary_writer_should_accumulate_entries_across_turns()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-summary-integration");
        using var provider = CreateServiceProvider(workspace.Path);

        var writer = provider.GetRequiredService<IChannelThreadSummaryWriter>();
        var binding = BuildBinding("binding-summary-002");
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);

        await writer.WriteAsync(binding, new ChannelTurnOutcome(
            Kind: ChannelTurnOutcomeKind.Delivered,
            Summary: "Turn one.",
            OccurredAt: now));
        await writer.WriteAsync(binding, new ChannelTurnOutcome(
            Kind: ChannelTurnOutcomeKind.ApprovalRequested,
            Summary: "Turn two needs approval.",
            OccurredAt: now.AddMinutes(5)));

        var filePath = Path.Combine(
            workspace.Path, "workspace", "channels", "binding-summary-002", "SUMMARY.md");
        var content = await File.ReadAllTextAsync(filePath);

        content.Should().Contain("Turn one.");
        content.Should().Contain("Turn two needs approval.");
        content.Should().Contain("delivered");
        content.Should().Contain("approval_requested");
    }

    private static ServiceProvider CreateServiceProvider(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        services.AddKodaClawChannelHub();
        return services.BuildServiceProvider();
    }

    private static ThreadBinding BuildBinding(string id)
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        return new ThreadBinding(
            Id: id,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "account-001",
            ExternalThreadId: $"thread-{id}",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: $"session-{id}",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity($"user-{id}", "TestUser", null),
            PolicyId: "policy-001",
            DeliveryRuleId: "rule-001",
            CreatedAt: now,
            UpdatedAt: now);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
