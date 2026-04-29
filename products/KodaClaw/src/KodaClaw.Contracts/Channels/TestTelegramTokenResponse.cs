namespace KodaClaw.Contracts.Channels;

public sealed record TestTelegramTokenResponse(
    bool Ok,
    string? BotName = null,
    string? BotUsername = null,
    string? Error = null);
