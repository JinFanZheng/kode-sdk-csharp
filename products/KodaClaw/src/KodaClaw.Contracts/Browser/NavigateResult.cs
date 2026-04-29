namespace KodaClaw.Contracts.Browser;

public sealed record NavigateResult(string Url, string TabId, string? DeviceId = null);
