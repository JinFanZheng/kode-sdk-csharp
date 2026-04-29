namespace KodaClaw.Contracts.Browser;

public sealed record CookieInfo(
    string Name,
    string Domain,
    string? Value = null,
    bool IsHttpOnly = false,
    bool IsSecure = false,
    DateTimeOffset? Expires = null);
