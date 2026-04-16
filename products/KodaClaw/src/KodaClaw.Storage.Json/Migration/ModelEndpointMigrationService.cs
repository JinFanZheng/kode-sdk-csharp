using System.Text.Json;
using KodaClaw.Contracts;

namespace KodaClaw.Storage.Json.Migration;

/// <summary>
/// One-time migration: converts legacy config/models/*.json (ModelEndpoint format)
/// into config/accounts/ + config/account-models/ (ProviderAccount + AccountModel).
/// Runs at application startup; idempotent (skips when config/accounts/ already exists
/// or config/models/ is absent).
/// </summary>
public sealed class ModelEndpointMigrationService
{
    private readonly string _workspaceRoot;
    private readonly IProviderAccountRepository _repo;
    private readonly IDiagnosticsService? _diagnostics;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ModelEndpointMigrationService(
        string workspaceRoot,
        IProviderAccountRepository repo,
        IDiagnosticsService? diagnostics = null)
    {
        _workspaceRoot = workspaceRoot;
        _repo = repo;
        _diagnostics = diagnostics;
    }

    public async Task MigrateIfNeededAsync(CancellationToken ct = default)
    {
        var oldDir = Path.Combine(_workspaceRoot, "config", "models");
        var newDir = Path.Combine(_workspaceRoot, "config", "accounts");

        // Already migrated or no old data
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir))
            return;

        var oldFiles = Directory.GetFiles(oldDir, "*.json");
        if (oldFiles.Length == 0)
            return;

        var oldEndpoints = LoadOldEndpoints(oldFiles);
        if (oldEndpoints.Count == 0)
            return;

        // Group by (ProviderKind, BaseUrl, ApiKeySecretRef)
        var groups = oldEndpoints
            .GroupBy(e => (e.Provider, BaseUrl: e.BaseUrl ?? "", SecretRef: e.ApiKeySecretRef ?? ""));

        var accountCount = 0;
        foreach (var group in groups)
        {
            var first = group.First();
            var account = new ProviderAccount(
                Id: $"account-{Guid.NewGuid():N}",
                DisplayName: InferDisplayName(first),
                ProviderKind: first.Provider,
                BaseUrl: first.BaseUrl,
                ApiKeySecretRef: first.ApiKeySecretRef,
                ApiKeyEnvironmentVariable: first.ApiKeyEnvironmentVariable,
                AccessMode: InferAccessMode(first),
                Enabled: group.Any(e => e.Enabled),
                CreatedAt: group.Min(e => e.CreatedAt),
                UpdatedAt: DateTimeOffset.UtcNow,
                CustomHeaders: first.CustomHeaders);

            await _repo.AddAccountAsync(account, ct);
            accountCount++;

            var isFirstModel = true;
            foreach (var endpoint in group)
            {
                var model = new AccountModel(
                    Id: $"model-{Guid.NewGuid():N}",
                    AccountId: account.Id,
                    DisplayName: endpoint.DisplayName,
                    ModelId: endpoint.ModelId,
                    Capabilities: endpoint.Capabilities,
                    IsDefaultForAccount: endpoint.IsDefault || isFirstModel,
                    IsGlobalDefault: endpoint.IsDefault,
                    Enabled: endpoint.Enabled,
                    CreatedAt: endpoint.CreatedAt,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    ContextWindowSize: endpoint.ContextWindowSize,
                    MaxOutputTokens: endpoint.MaxOutputTokens,
                    IsReasoning: endpoint.IsReasoning,
                    SupportsToolCalling: endpoint.SupportsToolCalling);

                await _repo.AddModelAsync(model, ct);
                isFirstModel = false;
            }
        }

        // Backup old directory
        var backupDir = Path.Combine(_workspaceRoot, "config", "models.migrated");
        if (!Directory.Exists(backupDir))
            Directory.Move(oldDir, backupDir);

        _diagnostics?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "model_migration",
            EventType: "migration_completed",
            Level: "Information",
            Message: $"Migrated {oldEndpoints.Count} endpoints to {accountCount} accounts.",
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string?>
            {
                ["endpointCount"] = oldEndpoints.Count.ToString(),
                ["accountCount"] = accountCount.ToString(),
            }));
    }

    private static List<LegacyEndpoint> LoadOldEndpoints(string[] files)
    {
        var result = new List<LegacyEndpoint>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var ep = JsonSerializer.Deserialize<LegacyEndpoint>(json, JsonOptions);
                if (ep is not null && !string.IsNullOrWhiteSpace(ep.Id))
                    result.Add(ep);
            }
            catch
            {
                // Skip corrupted files
            }
        }
        return result;
    }

    private static string InferDisplayName(LegacyEndpoint e)
    {
        return e.Provider switch
        {
            ModelProviderKind.Anthropic => "Anthropic",
            ModelProviderKind.OpenAI => "OpenAI",
            ModelProviderKind.OpenAIResponses => "OpenAI (Responses)",
            ModelProviderKind.AnthropicCompatible when IsChineseCodingPlan(e.BaseUrl) =>
                InferChineseProviderName(e.BaseUrl) + " (Coding Plan)",
            ModelProviderKind.OpenAICompatible => InferChineseProviderName(e.BaseUrl) ?? e.DisplayName,
            ModelProviderKind.AnthropicCompatible => e.DisplayName,
            _ => e.DisplayName,
        };
    }

    private static string? InferChineseProviderName(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (baseUrl.Contains("bigmodel.cn")) return "智谱 GLM";
        if (baseUrl.Contains("minimaxi.com")) return "MiniMax";
        if (baseUrl.Contains("xiaomimimo.com")) return "小米 MiMo";
        if (baseUrl.Contains("deepseek.com")) return "DeepSeek";
        if (baseUrl.Contains("moonshot") || baseUrl.Contains("kimi")) return "Kimi";
        return null;
    }

    private static string InferAccessMode(LegacyEndpoint e)
    {
        if (e.Provider == ModelProviderKind.AnthropicCompatible && IsChineseCodingPlan(e.BaseUrl))
            return "coding-plan";
        return "api";
    }

    private static bool IsChineseCodingPlan(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        return baseUrl.Contains("bigmodel.cn/api/anthropic")
            || baseUrl.Contains("minimaxi.com/anthropic")
            || baseUrl.Contains("token-plan-cn");
    }

    /// <summary>
    /// Minimal record matching the old ModelEndpoint JSON shape for deserialization.
    /// </summary>
    private sealed record LegacyEndpoint(
        string Id,
        string DisplayName,
        ModelProviderKind Provider,
        string ModelId,
        string? BaseUrl,
        string? ApiKeyEnvironmentVariable,
        string? ApiKeySecretRef,
        bool Enabled,
        ModelCapabilitySet Capabilities,
        bool IsDefault,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int ContextWindowSize = 128_000,
        int MaxOutputTokens = 8192,
        bool IsReasoning = false,
        bool SupportsToolCalling = true,
        IReadOnlyDictionary<string, string>? CustomHeaders = null);
}
