using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace;

public sealed class OnboardingStateService
{
    private const string OnboardingStatePath = "config/onboarding.json";
    private const string DiagnosticSource = "koda.workspace";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public OnboardingStateService(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        _workspaceService = workspaceService;
        _diagnosticsService = diagnosticsService;
    }

    private string GetAbsolutePath() =>
        Path.Combine(_workspaceService.RootPath, OnboardingStatePath);

    public async Task<OnboardingState> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var path = GetAbsolutePath();
            if (!File.Exists(path))
                return new OnboardingState { StartedAt = DateTimeOffset.UtcNow };

            var content = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrEmpty(content))
                return new OnboardingState { StartedAt = DateTimeOffset.UtcNow };

            return JsonSerializer.Deserialize<OnboardingState>(content, JsonOptions)
                ?? new OnboardingState { StartedAt = DateTimeOffset.UtcNow };
        }
        catch (JsonException ex)
        {
            RecordDiagnostic("onboarding.state.read_failed", "warning",
                $"Failed to parse onboarding state: {ex.Message}");
            return new OnboardingState { StartedAt = DateTimeOffset.UtcNow };
        }
        catch (IOException ex)
        {
            RecordDiagnostic("onboarding.state.read_failed", "warning",
                $"Failed to read onboarding state file: {ex.Message}");
            return new OnboardingState { StartedAt = DateTimeOffset.UtcNow };
        }
    }

    public async Task SaveStateAsync(OnboardingState state, CancellationToken ct = default)
    {
        var path = GetAbsolutePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(path, content, ct);
    }

    public async Task<OnboardingState> CompleteAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        var completed = state with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow };
        await SaveStateAsync(completed, ct);
        RecordDiagnostic("onboarding.completed", "info", "Onboarding completed.");
        return completed;
    }

    public async Task<OnboardingState> ResetAsync(CancellationToken ct = default)
    {
        var reset = new OnboardingState { StartedAt = DateTimeOffset.UtcNow };
        await SaveStateAsync(reset, ct);
        RecordDiagnostic("onboarding.reset", "info", "Onboarding state reset.");
        return reset;
    }

    private void RecordDiagnostic(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
