using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Repair;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class StartupRepairContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Startup_repair_report_should_json_round_trip()
    {
        var payload = new StartupRepairReportResponse(
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 13, 30, 0, TimeSpan.Zero),
            WorkspaceRootPath: "/tmp/kodaclaw",
            ReportPath: "/tmp/kodaclaw/config/startup-repair-report.json",
            Checklist: new RepairChecklist(
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 13, 30, 0, TimeSpan.Zero),
                Scope: "startupRepair",
                Summary: new RepairChecklistSummary(
                    TotalCount: 2,
                    BlockingCount: 0,
                    ActionRequiredCount: 1,
                    WarningCount: 1,
                    InfoCount: 0),
                Items:
                [
                    new RepairChecklistItem(
                        Id: "approval-canceled:approval-001",
                        Severity: RepairChecklistSeverity.Warning,
                        State: RepairChecklistState.Completed,
                        Category: "approvals",
                        Title: "Canceled stale runtime approval",
                        Summary: "Approval 'Dangerous tool' was pending when the process exited.",
                        Resource: "approval-001"),
                    new RepairChecklistItem(
                        Id: "active-main-session-reset:main-001",
                        Severity: RepairChecklistSeverity.ActionRequired,
                        State: RepairChecklistState.Completed,
                        Category: "sessions",
                        Title: "Cleared active main session after unsupported approval wait",
                        Summary: "Main session 'main-001' was waiting for approvals when the process exited.",
                        Resource: "main-001",
                        Action: "Inspect the old session before replaying work.")
                ]));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<StartupRepairReportResponse>(json, JsonOptions);

        json.Should().Contain("\"reportPath\":\"/tmp/kodaclaw/config/startup-repair-report.json\"");
        json.Should().Contain("\"scope\":\"startupRepair\"");
        json.Should().Contain("\"severity\":\"ActionRequired\"");
        roundTrip.Should().BeEquivalentTo(payload);
    }
}
