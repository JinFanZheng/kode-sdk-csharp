using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ControlPlane;

public sealed class SqliteInboxRepositoryIntegrationTests
{
    [Fact]
    public async Task Service_registration_should_persist_inbox_items_across_service_provider_restarts()
    {
        using var workspace = new TempWorkspaceRoot();
        var createdAt = new DateTimeOffset(2026, 3, 18, 15, 0, 0, TimeSpan.Zero);
        var expected = new InboxItem(
            Id: "inbox-integration-001",
            Kind: InboxItemKind.AutomationResult,
            Status: InboxItemStatus.Open,
            Title: "Daily summary ready",
            Summary: "Heartbeat finished successfully.",
            Source: "automation.heartbeat",
            CreatedAt: createdAt,
            UpdatedAt: createdAt,
            RequiresAction: false,
            Route: "/inbox/inbox-integration-001",
            SessionId: "session-automation",
            CorrelationId: "corr-integration-001",
            PayloadJson: """{"runId":"run-001"}""");

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IInboxRepository>();
            await repository.UpsertAsync(expected);
        }

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IInboxRepository>();
            var reloaded = await repository.GetByIdAsync(expected.Id);

            reloaded.Should().Be(expected);
        }
    }

    [Fact]
    public async Task Service_registration_should_expose_query_filters_for_requires_action_and_session()
    {
        using var workspace = new TempWorkspaceRoot();
        var baseTime = new DateTimeOffset(2026, 3, 18, 16, 0, 0, TimeSpan.Zero);

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IInboxRepository>();
            await repository.UpsertAsync(new InboxItem(
                Id: "inbox-action",
                Kind: InboxItemKind.Approval,
                Status: InboxItemStatus.Open,
                Title: "Approve outbound email",
                Summary: "Send response to partner.",
                Source: "channel.email",
                CreatedAt: baseTime,
                UpdatedAt: baseTime,
                RequiresAction: true,
                SessionId: "session-main"));
            await repository.UpsertAsync(new InboxItem(
                Id: "inbox-info",
                Kind: InboxItemKind.Information,
                Status: InboxItemStatus.Open,
                Title: "Webhook connected",
                Summary: "Channel binding is active.",
                Source: "channel.webhook",
                CreatedAt: baseTime.AddMinutes(1),
                UpdatedAt: baseTime.AddMinutes(1),
                RequiresAction: false,
                SessionId: "session-channel"));
        }

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IInboxRepository>();
            var filtered = await repository.ListAsync(new InboxQuery(
                Status: InboxItemStatus.Open,
                RequiresAction: true,
                SessionId: "session-main",
                Limit: 10));

            filtered.Select(item => item.Id).Should().Equal("inbox-action");
        }
    }

    private static ServiceProvider CreateServiceProvider(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        return services.BuildServiceProvider();
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-inbox-integration",
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
