namespace KodaClaw.Contracts.Workspace;

public sealed record WorkspaceAppConfig
{
    public int WorkspaceVersion { get; init; } = KodaClawWorkspaceLayout.CurrentWorkspaceVersion;

    public bool BootstrapCompleted { get; init; }

    public string? ActiveMainSessionId { get; init; }

    public int AutoSessionRetentionDays { get; init; } = 30;

    public int AutoSessionRetentionMaxPerTask { get; init; } = 20;

    public DateTimeOffset? LastConsolidationAt { get; init; }
}
