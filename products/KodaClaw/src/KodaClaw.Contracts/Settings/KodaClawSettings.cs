namespace KodaClaw.Contracts.Settings;

public sealed record KodaClawSettings(
    string DefaultLandingRoute,
    ThemeMode Theme,
    bool RequireApprovalForExternalActions,
    bool NotificationsEnabled,
    bool QuietHoursEnabled,
    string? QuietHoursStartLocalTime,
    string? QuietHoursEndLocalTime,
    DateTimeOffset UpdatedAt,
    bool AutomationsEnabled = false,
    bool AutoApproveToolCalls = false,
    int? MainMaxIterations = null,
    int? ChannelMaxIterations = null,
    int? AutomationMaxIterations = null)
{
    public static KodaClawSettings Default { get; } = new(
        DefaultLandingRoute: "/chat",
        Theme: ThemeMode.System,
        RequireApprovalForExternalActions: false,
        NotificationsEnabled: true,
        QuietHoursEnabled: false,
        QuietHoursStartLocalTime: null,
        QuietHoursEndLocalTime: null,
        UpdatedAt: DateTimeOffset.UnixEpoch,
        AutomationsEnabled: true,
        AutoApproveToolCalls: true,
        MainMaxIterations: 150,
        ChannelMaxIterations: 150,
        AutomationMaxIterations: null);
}
