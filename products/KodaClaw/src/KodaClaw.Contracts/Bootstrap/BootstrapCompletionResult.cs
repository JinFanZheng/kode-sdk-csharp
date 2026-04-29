namespace KodaClaw.Contracts.Bootstrap;

public sealed record BootstrapCompletionResult(
    string WorkspaceRootPath,
    bool BootstrapCompleted,
    string IdentityFilePath,
    string SoulFilePath,
    string UserFilePath,
    bool BootstrapFileArchived);
