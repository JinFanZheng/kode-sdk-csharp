namespace KodaClaw.Contracts.Workspace;

public sealed record OnboardingState
{
    public bool IsCompleted { get; init; }
    public string? CurrentStepId { get; init; }
    public string[] CompletedSteps { get; init; } = [];
    public string? SelectedLanguage { get; init; }
    public string? SelectedPresetId { get; init; }
    public string? SelectedPersonaPresetId { get; init; }
    public bool ChannelStepSkipped { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
}
