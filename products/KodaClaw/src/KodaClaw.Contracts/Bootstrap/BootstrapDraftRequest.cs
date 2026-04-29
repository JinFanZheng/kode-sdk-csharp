namespace KodaClaw.Contracts.Bootstrap;

public sealed record BootstrapDraftRequest(
    IReadOnlyList<BootstrapDraftMessage>? Conversation,
    string? IdentityMarkdown = null,
    string? SoulMarkdown = null,
    string? UserMarkdown = null);
