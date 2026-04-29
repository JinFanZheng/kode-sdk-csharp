using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ControlPlane;

public sealed class SqliteApprovalRepositoryIntegrationTests
{
    [Fact]
    public async Task Service_registration_should_persist_approvals_across_service_provider_restarts()
    {
        using var workspace = new TempWorkspaceRoot();
        var requestedAt = new DateTimeOffset(2026, 3, 18, 15, 0, 0, TimeSpan.Zero);
        var expected = new Approval(
            Id: "approval-integration-001",
            Kind: ApprovalKind.AutomationAction,
            Status: ApprovalStatus.Pending,
            Title: "Automated result needs approval",
            Summary: "Automation action awaits confirmation.",
            Source: "automation.scheduler",
            RequestedAt: requestedAt,
            UpdatedAt: requestedAt,
            SessionId: "session-automation",
            CorrelationId: "corr-integration-001",
            PayloadJson: """{""runId"":""run-001""}""");

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IApprovalRepository>();
            await repository.UpsertAsync(expected);
        }

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IApprovalRepository>();
            var reloaded = await repository.GetByIdAsync(expected.Id);

            reloaded.Should().Be(expected);
        }
    }

    [Fact]
    public async Task Service_registration_should_expose_filters_for_status_kind_and_session()
    {
        using var workspace = new TempWorkspaceRoot();
        var baseTime = new DateTimeOffset(2026, 3, 18, 16, 0, 0, TimeSpan.Zero);

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IApprovalRepository>();
            await repository.UpsertAsync(new Approval(
                Id: "approval-action",
                Kind: ApprovalKind.ExternalAction,
                Status: ApprovalStatus.Pending,
                Title: "Confirm external task",
                Summary: "External tool needs operator okay.",
                Source: "automation.external",
                RequestedAt: baseTime,
                UpdatedAt: baseTime,
                SessionId: "session-main"));
            await repository.UpsertAsync(new Approval(
                Id: "approval-other",
                Kind: ApprovalKind.OutboundMessage,
                Status: ApprovalStatus.Pending,
                Title: "Outbound message",
                Summary: "Another action pending.",
                Source: "channel.webhook",
                RequestedAt: baseTime.AddMinutes(1),
                UpdatedAt: baseTime.AddMinutes(1),
                SessionId: "session-other"));
        }

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<IApprovalRepository>();
            var filtered = await repository.ListAsync(new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                Kind: ApprovalKind.ExternalAction,
                SessionId: "session-main",
                Limit: 10));

            filtered.Should().ContainSingle().Which.Id.Should().Be("approval-action");
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
                "kodaclaw-approval-integration",
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
