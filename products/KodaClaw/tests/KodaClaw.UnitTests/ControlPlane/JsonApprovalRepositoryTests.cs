using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

public sealed class JsonApprovalRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonApprovalRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_approval()
    {
        var repository = CreateRepository();
        var requestedAt = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
        var approval = new Approval(
            Id: "approval-001",
            Kind: ApprovalKind.OutboundMessage,
            Status: ApprovalStatus.Pending,
            Title: "Approve outbound reply",
            Summary: "Control-plane ready to send.",
            Source: "channel.telegram",
            RequestedAt: requestedAt,
            UpdatedAt: requestedAt,
            SessionId: "session-main",
            CorrelationId: "corr-001",
            InboxItemId: "inbox-001",
            PayloadJson: """{""channel"":""telegram""}""");

        await repository.UpsertAsync(approval);

        var reloaded = await repository.GetByIdAsync(approval.Id);
        var listed = await repository.ListAsync(new ApprovalQuery(Status: ApprovalStatus.Pending, Limit: 10));

        reloaded.Should().Be(approval);
        listed.Should().ContainSingle().Which.Should().Be(approval);
    }

    [Fact]
    public async Task Repository_should_filter_approvals_and_reject_repeat_transitions()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 18, 13, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new Approval(
            Id: "approval-pending",
            Kind: ApprovalKind.OutboundEmail,
            Status: ApprovalStatus.Pending,
            Title: "Approve email",
            Summary: "Customer requested review.",
            Source: "channel.email",
            RequestedAt: baseTime,
            UpdatedAt: baseTime,
            SessionId: "session-main"));
        await repository.UpsertAsync(new Approval(
            Id: "approval-other",
            Kind: ApprovalKind.PluginAuthorization,
            Status: ApprovalStatus.Pending,
            Title: "Install plugin",
            Summary: "Await security approval.",
            Source: "plugin.manager",
            RequestedAt: baseTime.AddMinutes(1),
            UpdatedAt: baseTime.AddMinutes(1),
            SessionId: "session-other"));

        var transitionTime = baseTime.AddMinutes(5);
        var transitioned = await repository.TransitionAsync(
            "approval-pending",
            ApprovalStatus.Approved,
            transitionTime,
            decidedBy: "alice",
            decisionNote: "Looks good.");

        var filtered = await repository.ListAsync(new ApprovalQuery(
            Status: ApprovalStatus.Approved,
            Kind: ApprovalKind.OutboundEmail,
            SessionId: "session-main",
            Limit: 10));

        var reloaded = await repository.GetByIdAsync("approval-pending");
        transitioned.Should().BeTrue();
        filtered.Should().ContainSingle().Which.Id.Should().Be("approval-pending");
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(ApprovalStatus.Approved);
        reloaded.DecidedAt.Should().Be(transitionTime);
        reloaded.DecidedBy.Should().Be("alice");
        reloaded.DecisionNote.Should().Be("Looks good.");

        var secondTransition = await repository.TransitionAsync(
            "approval-pending",
            ApprovalStatus.Rejected,
            transitionTime.AddMinutes(1),
            decidedBy: "bob",
            decisionNote: "Needs follow-up.");
        secondTransition.Should().BeFalse();
    }
}
