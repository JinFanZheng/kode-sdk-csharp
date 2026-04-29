namespace KodaClaw.Contracts.Plugins;

public sealed record PluginPermissionRiskSummary(
    IReadOnlyList<string> HighRiskReasons,
    IReadOnlyList<string> MediumRiskReasons)
{
    public bool HasHighRisk => HighRiskReasons.Count > 0;
}
