namespace KodaClaw.Contracts.Bootstrap;

public sealed record BootstrapCompletionRequest(
    string IdentityMarkdown,
    string SoulMarkdown,
    string UserMarkdown,
    bool ArchiveBootstrapFile = true);
