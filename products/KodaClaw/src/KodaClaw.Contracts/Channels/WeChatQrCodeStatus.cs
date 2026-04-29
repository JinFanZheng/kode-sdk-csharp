namespace KodaClaw.Contracts.Channels;

/// <summary>
/// status: "wait" | "scaned" | "confirmed" | "expired"
/// </summary>
public sealed record WeChatQrCodeStatus(string Status, string? BotToken);
