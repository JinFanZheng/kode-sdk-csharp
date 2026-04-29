using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace;

public sealed class BootstrapService : IBootstrapService
{
    private const string BootstrapArchiveSuffix = ".archived";
    private const string DiagnosticSource = "koda.workspace";

    private readonly IWorkspaceService workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public BootstrapService(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        this.workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _diagnosticsService = diagnosticsService;
    }

    public async Task<BootstrapCompletionResult> CompleteAsync(
        BootstrapCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.IdentityMarkdown);
        ArgumentNullException.ThrowIfNull(request.SoulMarkdown);
        ArgumentNullException.ThrowIfNull(request.UserMarkdown);

        await workspaceService.EnsureInitializedAsync(cancellationToken);

        var identityPath = GetWorkspaceFilePath(KodaClawWorkspaceLayout.IdentityFile);
        var soulPath = GetWorkspaceFilePath(KodaClawWorkspaceLayout.SoulFile);
        var userPath = GetWorkspaceFilePath(KodaClawWorkspaceLayout.UserFile);

        await WriteTextAsync(identityPath, request.IdentityMarkdown, cancellationToken);
        await WriteTextAsync(soulPath, request.SoulMarkdown, cancellationToken);
        await WriteTextAsync(userPath, request.UserMarkdown, cancellationToken);

        var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);
        var updatedConfig = appConfig with { BootstrapCompleted = true };
        await workspaceService.SaveAppConfigAsync(updatedConfig, cancellationToken);

        var bootstrapFileArchived = ArchiveOrDeleteBootstrapFile(request.ArchiveBootstrapFile);

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: DiagnosticSource,
            EventType: "workspace.bootstrap.files_written",
            Level: "info",
            Message: "Bootstrap completed: identity, soul, and user files written.",
            Timestamp: DateTimeOffset.UtcNow));

        return new BootstrapCompletionResult(
            workspaceService.RootPath,
            updatedConfig.BootstrapCompleted,
            identityPath,
            soulPath,
            userPath,
            bootstrapFileArchived);
    }

    private bool ArchiveOrDeleteBootstrapFile(bool archive)
    {
        var bootstrapPath = GetWorkspaceFilePath(KodaClawWorkspaceLayout.BootstrapFile);
        if (!File.Exists(bootstrapPath))
        {
            return false;
        }

        if (!archive)
        {
            File.Delete(bootstrapPath);
            return false;
        }

        var archivePath = bootstrapPath + BootstrapArchiveSuffix;
        var directory = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(bootstrapPath, archivePath);
        return true;
    }

    private static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private string GetWorkspaceFilePath(string fileName)
    {
        return Path.Combine(
            workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            fileName);
    }
}
