# Provider Account 重构计划 — 一个提供商绑定多个模型

> 创建日期：2026-04-13
> 状态：DRAFT（已审查，待实施）
> 类型：优化/重构（大改，跨多模块）
> 原则：**不做向后兼容，按最佳实践重构到位；仅保留数据迁移路径**

---

## 1. 背景与动机

### 当前问题

当前模型管理以 `ModelEndpoint` 为原子单位，每个 endpoint 绑定**一个 Provider + 一个 Model**：

1. **配置冗余**：用户使用 Anthropic 的 Sonnet 和 Opus，需要创建两个 endpoint，各填一遍 ApiKey / BaseUrl
2. **连接浪费**：`RegistryAwareModelProvider` 按 `{endpointId}:{apiKey}` 缓存 provider 实例，相同 provider 不同 endpoint 创建多个 HTTP 连接池
3. **模型切换不灵活**：session 只能取全局默认 endpoint，无法在同一 provider 内按需切换模型
4. **国内 Coding Plan 兼容性差**：同一厂商（如智谱）同时提供标准 API 和 Coding Plan（Anthropic 兼容协议），两者 BaseUrl / ApiKey 完全不同，用户需理解底层协议差异
5. **定价信息是自由文本**：`CostHint` 格式不统一，无法做价格对比或成本估算

### 目标

引入 **Provider Account**（提供商账号）概念：一个账号 = 一套凭证 + 多个模型 + 结构化定价。

用户体验目标：
- 添加一个 Anthropic 账号 → 自动可选 Sonnet / Opus / Haiku，定价一目了然
- 添加一个智谱 Coding Plan 账号 → 自动可选 GLM-5.1 / GLM-5-Turbo
- Session 可在同一账号内按场景切换模型

### 重构原则

- **不保留旧类型**：`ModelEndpoint`、`IModelRegistryRepository`、`CreateModelEndpointRequest` 等全部删除
- **不保留旧端点**：`/api/models` 系列端点全部替换为 `/api/provider-accounts` 系列
- **不保留旧字段**：`CostHint` 替换为 `ModelPricing`，不做 fallback
- **只保留数据迁移**：启动时自动将旧 `config/models/*.json` 迁移到新格式

---

## 2. 现状分析

### 2.1 待删除的类型和文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `KodaClaw.Contracts/Models/ModelEndpoint.cs` | record | 旧一对一 endpoint |
| `KodaClaw.Contracts/Models/IModelRegistryRepository.cs` | interface | 旧仓储接口 |
| `KodaClaw.Contracts/Models/CreateModelEndpointRequest.cs` | record | 旧创建请求 |
| `KodaClaw.Contracts/Models/UpdateModelEndpointRequest.cs` | record | 旧更新请求 |
| `KodaClaw.Contracts/Models/ModelsQueryResponse.cs` | record | 旧查询响应 |
| `KodaClaw.Storage.Json/Repositories/JsonModelRegistryRepository.cs` | class | 旧 JSON 仓储 |
| `KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs` | partial class | 旧 REST 端点 |
| `KodaClaw.Gateway/Models/ModelRegistrySeedService.cs` | class | 旧种子服务 |

### 2.2 待重构的引用点（源码 25 + 前端 12 + 测试 14 = 51 个文件）

| 文件 | 引用内容 | 改动方式 |
|------|---------|---------|
| `Runtime/Providers/RegistryAwareModelProvider.cs` | `IModelRegistryRepository`, `ModelEndpoint` | **删除**，替换为 `AccountAwareModelProvider` |
| `Runtime/Providers/RuntimeProviderSelection.cs` | `IModelRegistryRepository` | 改用新接口 |
| `Runtime/ServiceCollectionExtensions.cs` | DI 注册旧接口 | 注册新接口 |
| `Runtime/Sessions/MainSessionService.cs` | `IModelRegistryRepository`, `ResolveModelOrFallbackAsync` | 改用新接口 |
| `Runtime/Sessions/ChannelSessionService.cs` | 同上 | 同上 |
| `Runtime/Sessions/AutomationSessionService.cs` | 同上 | 同上 |
| `Runtime/Bootstrap/BootstrapDraftService.cs` | `ResolveModelOrFallbackAsync` | 改用新接口 |
| `Runtime/Tools/ConfigUpdateTool.cs` | `IModelRegistryRepository`, `ModelEndpoint` | 改用新接口 |
| `Gateway/Composition/GatewayApp.Composition.cs` | DI 注册 + 中间件空状态检查 | 改为新服务 |
| `Gateway/Bootstrap/ConfigBootstrapWriter.cs` | `ModelEndpoint` | 改为创建 Account + Model |
| `Gateway/Bootstrap/RuntimeConfigurationBootstrap.cs` | `ModelEndpoint` 直接反序列化 | 适配新 JSON 格式 |
| `Gateway/Bootstrap/ConfigBootstrapService.cs` | 注释引用 `ModelRegistrySeedService` | 更新注释和启动顺序 |
| `Gateway/Validation/GatewayApp.ModelValidation.cs` | `ModelEndpoint` | 改为 Account 级校验 |
| `Gateway/Models/ModelConnectionTestService.cs` | `ModelEndpoint` | 新增 Account 级测试 |
| `Gateway/Endpoints/GatewayApp.SetupEndpoints.cs` | `createModelEndpoint` 逻辑 | 改为 `createProviderAccount` |
| `Gateway/Endpoints/GatewayApp.SessionEndpoints.cs` | `ModelEndpoint` 引用 | 改为 `AccountModel` |
| `Gateway/Workspace/WorkspaceBackupService.cs` | `ModelEndpoint` 引用 | 改为新类型 |
| `Gateway/Diagnostics/SecretMigrationReportService.cs` | `ModelEndpoint` 引用 | 改为新类型 |
| `ChannelHub/Commands/ChannelCommandDispatcher.cs` | `ModelEndpoint` 引用 | 改为新类型 |
| `Contracts/Models/ModelPreset.cs` | `CostHint` 字段 | 删除，加 `Group` + `Pricing` |
| `Contracts/Sessions/SessionDetail.cs` | `ModelEndpoint` 引用 | 改为 `AccountModel` |
| `Storage.Json/ServiceCollectionExtensions.cs` | DI 注册旧 Repository | 注册新 Repository |
| `Gateway/Resources/model-presets.json` | `costHint` 字段 | → `pricing` + `group`（25 个 preset） |
| `Gateway/Models/ModelPresetService.cs` | 加载 preset | 支持 `group` + `pricing` |

前端和测试文件详见 §10 变更文件清单。

### 2.3 Coding Plan 现状

| 厂商 | Coding Plan BaseUrl | 标准 API BaseUrl |
|------|---------------------|---------|
| 智谱 GLM | `open.bigmodel.cn/api/anthropic` | `open.bigmodel.cn/api/paas/v4`（OpenAICompatible） |
| MiniMax | `api.minimaxi.com/anthropic` | `api.minimaxi.com/v1`（OpenAICompatible） |
| 小米 MiMo | `token-plan-cn.xiaomimimo.com/anthropic` | `api.xiaomimimo.com/v1`（OpenAICompatible） |

同一厂商的标准 API 和 Coding Plan：协议不同、Key 不同、BaseUrl 不同 → 必须是两个独立 Account。

---

## 3. 新数据模型

### 3.1 ProviderAccount + AccountModel + ModelPricing

```csharp
// ─── 提供商账号（凭证维度聚合）──────────────────────────────
public sealed record ProviderAccount(
    string Id,                                // "account-{guid:N}"
    string DisplayName,                       // "Anthropic" / "智谱 GLM (Coding Plan)"
    ModelProviderKind ProviderKind,            // Anthropic / OpenAICompatible / ...
    string? BaseUrl,                          // 共享 base URL
    string? ApiKeySecretRef,                  // Keychain 密钥引用
    string? ApiKeyEnvironmentVariable,        // 备用环境变量
    string? AccessMode,                       // null/"api" | "coding-plan"
    bool Enabled,                             // 账号级启禁用
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? CustomHeaders = null
);

// ─── 结构化定价 ─────────────────────────────────────────────
public sealed record ModelPricing(
    decimal? InputTokenPrice = null,          // 每 1M input tokens 价格
    decimal? OutputTokenPrice = null,         // 每 1M output tokens 价格
    string Currency = "USD",                  // "USD" | "CNY"
    string PricingModel = "pay-per-use",      // "pay-per-use" | "subscription"
    string? PricingNote = null                // 补充说明，如 "≤256K 上下文价格"
);

// ─── 账号下的模型 ───────────────────────────────────────────
public sealed record AccountModel(
    string Id,                                // "model-{guid:N}"
    string AccountId,                         // 所属 ProviderAccount.Id
    string DisplayName,                       // "Claude Sonnet 4.6"
    string ModelId,                           // "claude-sonnet-4-6"
    ModelCapabilitySet Capabilities,          // Text | Image
    bool IsDefaultForAccount,                 // 该账号内的默认模型
    bool IsGlobalDefault,                     // 全局默认（跨账号唯一）
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ContextWindowSize = 128_000,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    ModelPricing? Pricing = null
);
```

### 3.2 仓储接口（替代 IModelRegistryRepository）

```csharp
public interface IProviderAccountRepository
{
    // ── Account CRUD ─────────────────────────────────────────
    Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(CancellationToken ct = default);
    Task<ProviderAccount?> GetAccountByIdAsync(string id, CancellationToken ct = default);
    Task AddAccountAsync(ProviderAccount account, CancellationToken ct = default);
    Task<bool> UpdateAccountAsync(ProviderAccount account, CancellationToken ct = default);
    Task<bool> DeleteAccountAsync(string id, CancellationToken ct = default);

    // ── AccountModel CRUD ────────────────────────────────────
    Task<IReadOnlyList<AccountModel>> ListModelsAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<AccountModel>> ListAllModelsAsync(CancellationToken ct = default);
    Task<AccountModel?> GetModelByIdAsync(string modelId, CancellationToken ct = default);
    Task AddModelAsync(AccountModel model, CancellationToken ct = default);
    Task<bool> UpdateModelAsync(AccountModel model, CancellationToken ct = default);
    Task<bool> DeleteModelAsync(string modelId, CancellationToken ct = default);

    // ── 默认管理 + 能力匹配 ──────────────────────────────────
    Task<bool> SetGlobalDefaultAsync(string modelId, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// 返回第一个 enabled 且 Capabilities 包含所有 required 能力的模型及其所属账号。
    /// IsGlobalDefault=true 的优先，无匹配时返回 null。
    /// </summary>
    Task<ResolvedModel?> ResolveDefaultForAsync(
        ModelCapabilitySet required, CancellationToken ct = default);
}

/// <summary>
/// 解析结果：Account + Model 配对，供 Runtime 构建 provider。
/// </summary>
public sealed record ResolvedModel(
    ProviderAccount Account,
    AccountModel Model);
```

### 3.3 保留的类型

| 类型 | 说明 |
|------|------|
| `ModelProviderKind` | 枚举不变：OpenAI / Anthropic / OpenAICompatible / AnthropicCompatible / OpenAIResponses |
| `ModelCapabilitySet` | flags 枚举不变：Text / Image / Video / File / Audio |
| `ModelPreset` | 保留但重构字段（见 §7） |
| `ModelConnectionTestRequest` | **保留**：Onboarding 连通性测试仍需要（基于 preset 临时凭证测试，不依赖已持久化的 Account） |
| `ModelConnectionTestResponse` | **保留**：测试结果 DTO 不变 |

### 3.4 Coding Plan 设计

同一厂商的标准 API 和 Coding Plan 是两个独立 Account：

```
ProviderAccount "智谱 GLM (标准 API)"
  ├── ProviderKind: OpenAICompatible
  ├── AccessMode: "api"
  ├── BaseUrl: "https://open.bigmodel.cn/api/paas/v4"
  ├── ApiKeySecretRef: "keychain:accounts:account-abc"
  └── Models:
        ├── { ModelId: "glm-5",   Capabilities: Text }
        └── { ModelId: "glm-5.1", Capabilities: Text|Image }

ProviderAccount "智谱 GLM (Coding Plan)"
  ├── ProviderKind: AnthropicCompatible
  ├── AccessMode: "coding-plan"
  ├── BaseUrl: "https://open.bigmodel.cn/api/anthropic"
  ├── ApiKeySecretRef: "keychain:accounts:account-def"    ← 不同的 key
  └── Models:
        ├── { ModelId: "glm-5.1",     Capabilities: Text|Image }
        └── { ModelId: "GLM-5-Turbo", Capabilities: Text }
```

---

## 4. 运行时重构

### 4.1 RegistryAwareModelProvider → AccountAwareModelProvider

重命名并重写，直接依赖 `IProviderAccountRepository`：

```csharp
public sealed class AccountAwareModelProvider : IModelProvider
{
    private readonly IProviderAccountRepository _repo;
    private readonly ISecretStore _secretStore;
    private readonly IRuntimeModelProviderFactory _factory;
    private readonly DynamicModelProvider _fallback;
    private readonly IDiagnosticsService? _diagnostics;

    // 缓存 key 改为 accountId — 同 account 下的模型共享 provider 实例
    private readonly ConcurrentDictionary<string, IModelProvider> _providerCache = new();

    public string ProviderName => "account";

    private async Task<(IModelProvider Provider, ModelRequest Request)> ResolveAsync(
        ModelRequest request, CancellationToken ct)
    {
        var required = InferRequiredCapabilities(request);
        var resolved = await _repo.ResolveDefaultForAsync(required, ct);

        // 能力降级 fallback
        if (resolved is null && required != ModelCapabilitySet.Text)
        {
            resolved = await _repo.ResolveDefaultForAsync(ModelCapabilitySet.Text, ct);
            if (resolved is not null)
                request = StripUnsupportedContent(request, resolved.Model.Capabilities);
        }

        if (resolved is not null)
        {
            var provider = await BuildProviderAsync(resolved.Account, ct);
            var normalized = NormalizeRequest(request, resolved.Model);
            return (provider, normalized);
        }

        return (_fallback, request);
    }

    private async Task<IModelProvider> BuildProviderAsync(
        ProviderAccount account, CancellationToken ct)
    {
        var apiKey = await ResolveApiKeyAsync(account, ct);

        // 同一 account 共享 provider 实例 → 连接池复用
        var cacheKey = $"{account.Id}:{apiKey}";
        if (_providerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var (providerKind, snapshot) = account.ProviderKind switch
        {
            ModelProviderKind.Anthropic or ModelProviderKind.AnthropicCompatible =>
                (RuntimeProviderKind.Anthropic, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,   // model 在 NormalizeRequest 中设置
                    AnthropicApiKey: apiKey,
                    AnthropicBaseUrl: account.BaseUrl,
                    CustomHeaders: account.CustomHeaders,
                    OpenAIApiKey: null, OpenAIBaseUrl: null)),

            ModelProviderKind.OpenAI or ModelProviderKind.OpenAICompatible =>
                (RuntimeProviderKind.OpenAI, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,
                    OpenAIApiKey: apiKey,
                    OpenAIBaseUrl: account.BaseUrl,
                    CustomHeaders: account.CustomHeaders,
                    AnthropicApiKey: null, AnthropicBaseUrl: null)),

            ModelProviderKind.OpenAIResponses =>
                (RuntimeProviderKind.OpenAIResponses, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,
                    OpenAIApiKey: apiKey,
                    OpenAIBaseUrl: account.BaseUrl,
                    CustomHeaders: account.CustomHeaders,
                    AnthropicApiKey: null, AnthropicBaseUrl: null)),

            _ => throw new InvalidOperationException(
                $"Unsupported provider kind '{account.ProviderKind}' for account '{account.Id}'."),
        };

        var provider = _factory.Create(providerKind, snapshot);
        return _providerCache.GetOrAdd(cacheKey, provider);
    }

    private static ModelRequest NormalizeRequest(ModelRequest request, AccountModel model)
    {
        var normalized = request;

        // 覆写 model ID
        if (!string.IsNullOrWhiteSpace(model.ModelId))
            normalized = normalized with { Model = model.ModelId };

        // 设置 MaxTokens
        if (model.MaxOutputTokens > 0 && normalized.MaxTokens is null)
            normalized = normalized with { MaxTokens = model.MaxOutputTokens };

        // ToolCalling 校验
        if (!model.SupportsToolCalling && normalized.Tools is { Count: > 0 })
            throw new InvalidOperationException(
                $"Model '{model.DisplayName}' does not support tool calling.");

        return normalized;
    }

    private async Task<string> ResolveApiKeyAsync(ProviderAccount account, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(account.ApiKeySecretRef) &&
            SecretRef.TryParse(account.ApiKeySecretRef, out var secretRef))
        {
            var secret = await _secretStore.GetAsync(secretRef, ct);
            if (!string.IsNullOrWhiteSpace(secret)) return secret;
        }

        if (!string.IsNullOrWhiteSpace(account.ApiKeyEnvironmentVariable))
        {
            var envVal = Environment.GetEnvironmentVariable(account.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envVal)) return envVal;
        }

        throw new InvalidOperationException(
            $"No API key configured for provider account '{account.DisplayName}'.");
    }

    // InferRequiredCapabilities / StripUnsupportedContent 逻辑保持不变
}
```

**核心变化**：
- `BuildProviderAsync` 接收 `ProviderAccount` 而非 `ModelEndpoint`
- `NormalizeRequest` 接收 `AccountModel` 而非 `ModelEndpoint`
- 缓存 key 从 `endpointId:apiKey` 变为 `accountId:apiKey` → 同 account 模型共享连接池
- 不再需要从 endpoint 上取凭证，直接从 account 上取

### 4.2 RuntimeProviderSelector 简化

```csharp
internal static class RuntimeProviderSelector
{
    /// <summary>
    /// 优先从 IProviderAccountRepository 获取默认模型 ID，
    /// 若 registry 为空则 fallback 到环境变量配置。
    /// </summary>
    public static async Task<string> ResolveModelOrFallbackAsync(
        IRuntimeConfigurationResolver? resolver,
        string? fallbackModel,
        IProviderAccountRepository? accountRepo,
        CancellationToken ct = default)
    {
        try
        {
            return ResolveModelOrThrow(resolver, fallbackModel);
        }
        catch (InvalidOperationException) when (accountRepo is not null)
        {
            var resolved = await accountRepo.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, ct);

            if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.Model.ModelId))
                return resolved.Model.ModelId;

            throw;
        }
    }

    // Resolve(), ResolveModelOrThrow(), LooksLikeOpenAiModel(), LooksLikeAnthropicModel() 不变
}
```

### 4.3 Session 服务改动

三个 session 服务（`MainSessionService`, `ChannelSessionService`, `AutomationSessionService`）改动相同：

```csharp
// 原：
private readonly IModelRegistryRepository? _modelRegistryRepository;
private Task<string> ResolveConfiguredModelAsync(CancellationToken ct) =>
    RuntimeProviderSelector.ResolveModelOrFallbackAsync(
        _runtimeConfigurationResolver, null, _modelRegistryRepository, ct);

// 改为：
private readonly IProviderAccountRepository? _accountRepository;
private Task<string> ResolveConfiguredModelAsync(CancellationToken ct) =>
    RuntimeProviderSelector.ResolveModelOrFallbackAsync(
        _runtimeConfigurationResolver, null, _accountRepository, ct);
```

---

## 5. 存储层

### 5.1 JSON 文件结构

```
~/.kodaclaw/config/
  accounts/                        ← 新目录
    account-abc123.json            ← ProviderAccount（不含 models）
    account-def456.json
  account-models/                  ← 新目录
    model-111.json                 ← AccountModel (accountId: "account-abc123")
    model-222.json
    model-333.json
```

### 5.2 JsonProviderAccountRepository 实现

```csharp
public sealed class JsonProviderAccountRepository : JsonStoreBase, IProviderAccountRepository
{
    private readonly string _accountsDir;    // config/accounts/
    private readonly string _modelsDir;      // config/account-models/

    public async Task<ResolvedModel?> ResolveDefaultForAsync(
        ModelCapabilitySet required, CancellationToken ct = default)
    {
        var accounts = await ScanDirectoryAsync<ProviderAccount>(_accountsDir, null, ct);
        var allModels = await ScanDirectoryAsync<AccountModel>(_modelsDir, null, ct);

        var enabledAccounts = accounts.Where(a => a.Enabled).ToDictionary(a => a.Id);

        return allModels
            .Where(m => m.Enabled
                && enabledAccounts.ContainsKey(m.AccountId)
                && (m.Capabilities & required) == required)
            .OrderByDescending(m => m.IsGlobalDefault)
            .ThenBy(m => m.CreatedAt)
            .Select(m => new ResolvedModel(enabledAccounts[m.AccountId], m))
            .FirstOrDefault();
    }

    public async Task<bool> DeleteAccountAsync(string id, CancellationToken ct = default)
    {
        // 级联删除所有 models
        var models = await ListModelsAsync(id, ct);
        foreach (var m in models)
            await DeleteEntityAsync(ModelFilePath(m.Id), ct);
        await DeleteEntityAsync(AccountFilePath(id), ct);
        return true;
    }

    // ... 其他 CRUD 方法直接委托 JsonStoreBase
}
```

### 5.3 数据迁移服务

```csharp
public sealed class ModelEndpointMigrationService
{
    private readonly string _workspaceRoot;
    private readonly IProviderAccountRepository _repo;
    private readonly IDiagnosticsService? _diagnostics;

    /// <summary>
    /// 检测 config/models/ 下是否有旧 ModelEndpoint JSON 文件。
    /// 有则迁移到 config/accounts/ + config/account-models/，
    /// 旧目录重命名为 config/models.migrated/。
    /// </summary>
    public async Task MigrateIfNeededAsync(CancellationToken ct = default)
    {
        var oldDir = Path.Combine(_workspaceRoot, "config", "models");
        if (!Directory.Exists(oldDir)) return;

        var oldFiles = Directory.GetFiles(oldDir, "*.json");
        if (oldFiles.Length == 0) return;

        // 1. 加载旧 ModelEndpoint
        var oldEndpoints = LoadOldEndpoints(oldFiles);

        // 2. 按 (ProviderKind, BaseUrl, ApiKeySecretRef) 分组
        var groups = oldEndpoints
            .GroupBy(e => (e.Provider, e.BaseUrl ?? "", e.ApiKeySecretRef ?? ""));

        // 3. 每组创建一个 Account + N 个 Models
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

            foreach (var endpoint in group)
            {
                var model = new AccountModel(
                    Id: $"model-{Guid.NewGuid():N}",
                    AccountId: account.Id,
                    DisplayName: endpoint.DisplayName,
                    ModelId: endpoint.ModelId,
                    Capabilities: endpoint.Capabilities,
                    IsDefaultForAccount: endpoint.IsDefault,
                    IsGlobalDefault: endpoint.IsDefault,
                    Enabled: endpoint.Enabled,
                    CreatedAt: endpoint.CreatedAt,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    ContextWindowSize: endpoint.ContextWindowSize,
                    MaxOutputTokens: endpoint.MaxOutputTokens,
                    IsReasoning: endpoint.IsReasoning,
                    SupportsToolCalling: endpoint.SupportsToolCalling);

                await _repo.AddModelAsync(model, ct);
            }
        }

        // 4. 备份旧目录
        var backupDir = Path.Combine(_workspaceRoot, "config", "models.migrated");
        Directory.Move(oldDir, backupDir);

        _diagnostics?.Record(new DiagnosticEvent(
            Source: "model_migration",
            EventType: "migration_completed",
            Message: $"Migrated {oldEndpoints.Count} endpoints to {groups.Count()} accounts"));
    }

    private static string InferAccessMode(OldEndpoint e)
    {
        // AnthropicCompatible + 国内 BaseUrl → coding-plan
        if (e.Provider == ModelProviderKind.AnthropicCompatible &&
            e.BaseUrl is not null &&
            (e.BaseUrl.Contains("bigmodel.cn/api/anthropic") ||
             e.BaseUrl.Contains("minimaxi.com/anthropic") ||
             e.BaseUrl.Contains("token-plan-cn")))
            return "coding-plan";
        return "api";
    }
}
```

**边界情况**：
- 相同 Provider + BaseUrl 但不同 ApiKey → 拆成两个 Account
- 旧 endpoint 无任何密钥 → Account.Enabled = false
- `config/accounts/` 已存在 → 跳过迁移（幂等）
- 旧 Secret 引用格式 `keychain:models:model-xxx` → 迁移后创建新引用 `keychain:accounts:account-yyy`，旧 secret 值复制过来

---

## 6. Gateway API

### 6.1 端点设计（替代旧 /api/models）

```
# Account CRUD
GET    /api/provider-accounts                     → ProviderAccountResponse[]
GET    /api/provider-accounts/{id}                → ProviderAccountResponse
POST   /api/provider-accounts                     → ProviderAccountResponse（含批量 models）
PUT    /api/provider-accounts/{id}                → ProviderAccountResponse
DELETE /api/provider-accounts/{id}                → 204（级联删除所有 models + secret）

# Account 下的 Model CRUD
POST   /api/provider-accounts/{accountId}/models                → AccountModelResponse
PUT    /api/provider-accounts/{accountId}/models/{modelId}      → AccountModelResponse
DELETE /api/provider-accounts/{accountId}/models/{modelId}      → 204

# 全局默认
POST   /api/provider-accounts/default/{modelId}                 → AccountModelResponse

# 连通性测试
POST   /api/provider-accounts/{id}/test-connection              → ModelConnectionTestResponse

# Preset 查询（保留，但返回新格式）
GET    /api/models/presets                                      → ModelPreset[]
GET    /api/models/presets/{presetId}                            → ModelPreset
```

### 6.2 请求/响应 DTO

```csharp
public sealed record CreateProviderAccountRequest(
    string DisplayName,
    ModelProviderKind ProviderKind,
    string? BaseUrl,
    string? ApiKeyValue,                              // 临时，存入 Keychain 后丢弃
    string? ApiKeyEnvironmentVariable,
    string? AccessMode,                               // null/"api" | "coding-plan"
    IReadOnlyDictionary<string, string>? CustomHeaders,
    IReadOnlyList<CreateAccountModelRequest> Models    // 批量创建
);

public sealed record CreateAccountModelRequest(
    string DisplayName,
    string ModelId,
    ModelCapabilitySet Capabilities = ModelCapabilitySet.Text,
    int ContextWindowSize = 128_000,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    bool IsDefaultForAccount = false,
    bool IsGlobalDefault = false,
    ModelPricing? Pricing = null
);

public sealed record UpdateProviderAccountRequest(
    string? DisplayName = null,
    string? BaseUrl = null,
    string? ApiKeyValue = null,
    string? ApiKeyEnvironmentVariable = null,
    bool? Enabled = null,
    IReadOnlyDictionary<string, string>? CustomHeaders = null
);

public sealed record UpdateAccountModelRequest(
    string? DisplayName = null,
    string? ModelId = null,
    ModelCapabilitySet? Capabilities = null,
    int? ContextWindowSize = null,
    int? MaxOutputTokens = null,
    bool? IsReasoning = null,
    bool? SupportsToolCalling = null,
    bool? Enabled = null,
    ModelPricing? Pricing = null
);

public sealed record ProviderAccountResponse(
    string Id,
    string DisplayName,
    ModelProviderKind ProviderKind,
    string? BaseUrl,
    string? AccessMode,
    bool Enabled,
    bool HasApiKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AccountModelResponse> Models
);

public sealed record AccountModelResponse(
    string Id,
    string AccountId,
    string DisplayName,
    string ModelId,
    ModelCapabilitySet Capabilities,
    bool IsDefaultForAccount,
    bool IsGlobalDefault,
    bool Enabled,
    int ContextWindowSize,
    int MaxOutputTokens,
    bool IsReasoning,
    bool SupportsToolCalling,
    ModelPricing? Pricing
);
```

---

## 7. Preset 体系重构

### 7.1 ModelPreset 替换 CostHint 为 Pricing

```csharp
public sealed record ModelPreset(
    string PresetId,
    string DisplayName,
    string Provider,                          // "Anthropic" / "OpenAICompatible" / ...
    string ModelId,
    string? BaseUrl,
    int ContextWindowSize,
    string Tier,                              // "Recommended" | "Advanced" | "Fast" | "Reasoning" | "Local"
    string Description,
    bool RequiresBaseUrl,
    ModelCapabilitySet DefaultCapabilities = ModelCapabilitySet.Text,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    string? AccessMode = null,                // null/"api" | "coding-plan"
    string? AnthropicBaseUrl = null,
    string? Group = null,                     // ← 新增：provider 分组
    ModelPricing? Pricing = null              // ← 新增：替代 CostHint
);
```

### 7.2 model-presets.json 格式

```jsonc
// CostHint 全部替换为 Pricing
{
  "presetId": "anthropic-sonnet-4-6",
  "group": "anthropic-api",
  "displayName": "Claude Sonnet 4.6",
  "provider": "Anthropic",
  "modelId": "claude-sonnet-4-6",
  "contextWindowSize": 1000000,
  "maxOutputTokens": 64000,
  "tier": "Recommended",
  "description": "KodaClaw 推荐的默认模型，平衡智能与速度",
  "requiresBaseUrl": false,
  "defaultCapabilities": 3,
  "pricing": {
    "inputTokenPrice": 3.00,
    "outputTokenPrice": 15.00,
    "currency": "USD",
    "pricingModel": "pay-per-use"
  }
},
{
  "presetId": "deepseek-v3",
  "group": "deepseek-api",
  "displayName": "DeepSeek V3",
  "provider": "OpenAICompatible",
  "modelId": "deepseek-chat",
  "baseUrl": "https://api.deepseek.com/v1",
  "pricing": {
    "inputTokenPrice": 2.00,
    "outputTokenPrice": 8.00,
    "currency": "CNY",
    "pricingModel": "pay-per-use"
  }
},
{
  "presetId": "minimax-m2.7-coding",
  "group": "minimax-coding-plan",
  "displayName": "MiniMax M2.7",
  "provider": "AnthropicCompatible",
  "accessMode": "coding-plan",
  "pricing": {
    "pricingModel": "subscription",
    "pricingNote": "Token Plan 订阅制"
  }
}
```

### 7.3 Preset → Account 自动映射

Onboarding 时用户选一个 preset，前端根据 `group` 找到同组所有 preset 并批量创建：

```typescript
function deriveModelsFromPreset(
  selectedPreset: ModelPreset,
  allPresets: ModelPreset[]
): CreateAccountModelRequest[] {
  const group = selectedPreset.group;
  const siblings = group
    ? allPresets.filter(p => p.group === group)
    : [selectedPreset]; // 无 group 则只添加选中的

  return siblings.map(p => ({
    displayName: p.displayName,
    modelId: p.modelId,
    capabilities: p.defaultCapabilities,
    contextWindowSize: p.contextWindowSize,
    maxOutputTokens: p.maxOutputTokens,
    isReasoning: p.isReasoning,
    supportsToolCalling: p.supportsToolCalling,
    isDefaultForAccount: p.presetId === selectedPreset.presetId,
    isGlobalDefault: p.presetId === selectedPreset.presetId,
    pricing: p.pricing ?? undefined,
  }));
}
```

---

## 8. 前端重构

### 8.1 TypeScript 类型（替换旧类型）

```typescript
// ─── 删除的旧类型 ─────────────────────────────────────────
// ModelEndpoint, CreateModelEndpointRequest, UpdateModelEndpointRequest,
// ModelsQueryResponse — 全部删除

// ─── 新增类型 ──────────────────────────────────────────────
export interface ModelPricing {
  inputTokenPrice?: number;
  outputTokenPrice?: number;
  currency: 'USD' | 'CNY';
  pricingModel: 'pay-per-use' | 'subscription';
  pricingNote?: string;
}

export interface ProviderAccount {
  id: string;
  displayName: string;
  providerKind: ModelProviderKind;
  baseUrl: string | null;
  accessMode: 'api' | 'coding-plan' | null;
  enabled: boolean;
  hasApiKey: boolean;
  createdAt: string;
  updatedAt: string;
  models: AccountModel[];
}

export interface AccountModel {
  id: string;
  accountId: string;
  displayName: string;
  modelId: string;
  capabilities: number;
  isDefaultForAccount: boolean;
  isGlobalDefault: boolean;
  enabled: boolean;
  contextWindowSize: number;
  maxOutputTokens: number;
  isReasoning: boolean;
  supportsToolCalling: boolean;
  pricing?: ModelPricing;
}

// ModelPreset — costHint 删除，改为 pricing + group
export interface ModelPreset {
  presetId: string;
  displayName: string;
  provider: string;
  modelId: string;
  baseUrl?: string;
  contextWindowSize: number;
  tier: 'Recommended' | 'Advanced' | 'Fast' | 'Reasoning' | 'Local';
  description: string;
  requiresBaseUrl: boolean;
  defaultCapabilities: number;
  maxOutputTokens: number;
  isReasoning: boolean;
  supportsToolCalling: boolean;
  accessMode?: 'api' | 'coding-plan';
  anthropicBaseUrl?: string;
  group?: string;            // 新增
  pricing?: ModelPricing;    // 替代 costHint
}
```

### 8.2 API 调用层（替换旧函数）

```typescript
// ─── 删除旧函数 ───────────────────────────────────────────
// fetchModels, createModelEndpoint, updateModelEndpoint,
// deleteModelEndpoint, setDefaultModelEndpoint — 全部删除

// ─── 新增函数 ─────────────────────────────────────────────
export async function fetchProviderAccounts(signal?: AbortSignal): Promise<ProviderAccount[]>;
export async function createProviderAccount(req: CreateProviderAccountRequest, signal?: AbortSignal): Promise<ProviderAccount>;
export async function updateProviderAccount(id: string, req: UpdateProviderAccountRequest, signal?: AbortSignal): Promise<ProviderAccount>;
export async function deleteProviderAccount(id: string, signal?: AbortSignal): Promise<void>;
export async function addAccountModel(accountId: string, req: CreateAccountModelRequest, signal?: AbortSignal): Promise<AccountModel>;
export async function updateAccountModel(accountId: string, modelId: string, req: UpdateAccountModelRequest, signal?: AbortSignal): Promise<AccountModel>;
export async function deleteAccountModel(accountId: string, modelId: string, signal?: AbortSignal): Promise<void>;
export async function setGlobalDefaultModel(modelId: string, signal?: AbortSignal): Promise<AccountModel>;
export async function testProviderAccountConnection(id: string, signal?: AbortSignal): Promise<ModelConnectionTestResponse>;

// fetchModelPresets / testModelConnection 保留（preset 查询不变）
```

### 8.3 ModelsSettingsDesk 重写

两级结构 UI：

```
┌─────────────────────────────────────────────────────────────────┐
│ 模型管理                                              [+ 账号]  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─ Anthropic ──────────────────── api ───────── [编辑] [测试] ┐│
│  │  ● Claude Sonnet 4.6  Text|Image  默认  $3/$15 per M       ││
│  │  ○ Claude Opus 4.6    Text|Image        $5/$25 per M       ││
│  │  ○ Claude Haiku 4.5   Text              $1/$5 per M        ││
│  │                                                  [+ 模型]   ││
│  └─────────────────────────────────────────────────────────────┘│
│                                                                 │
│  ┌─ 智谱 GLM ──────────── coding-plan ──────── [编辑] [测试] ┐│
│  │  ● GLM-5.1            Text|Image  默认  订阅制              ││
│  │  ○ GLM-5-Turbo        Text              订阅制              ││
│  │                                                  [+ 模型]   ││
│  └─────────────────────────────────────────────────────────────┘│
│                                                                 │
│  ┌─ DeepSeek ──────────────── api ─────────── [编辑] [测试] ┐ │
│  │  ● DeepSeek V3        Text        默认  ¥2/¥8 per M       ││
│  │                                                  [+ 模型]   ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

定价格式化：

```typescript
function formatPricing(pricing?: ModelPricing): string {
  if (!pricing) return '—';
  if (pricing.pricingModel === 'subscription')
    return pricing.pricingNote ?? '订阅制';

  const sym = pricing.currency === 'CNY' ? '¥' : '$';
  const parts: string[] = [];
  if (pricing.inputTokenPrice != null) parts.push(`${sym}${pricing.inputTokenPrice}`);
  if (pricing.outputTokenPrice != null) parts.push(`${sym}${pricing.outputTokenPrice}`);
  const base = parts.join('/') + ' per M';
  return pricing.pricingNote ? `${base}（${pricing.pricingNote}）` : base;
}
```

---

## 9. 分阶段实施计划

### Phase 1：Contracts + Storage + 迁移

| 步骤 | 内容 |
|------|------|
| 1.1 | 新增 `ProviderAccount.cs`, `AccountModel.cs`, `ModelPricing.cs`, `ResolvedModel.cs` |
| 1.2 | 新增 `IProviderAccountRepository.cs`，定义完整接口 |
| 1.3 | 新增请求/响应 DTO（`CreateProviderAccountRequest` 等） |
| 1.4 | 删除 `ModelEndpoint.cs`, `IModelRegistryRepository.cs`, `CreateModelEndpointRequest.cs`, `UpdateModelEndpointRequest.cs`, `ModelsQueryResponse.cs` |
| 1.5 | 修改 `ModelPreset.cs`：删除 `CostHint`，新增 `Group`, `Pricing` |
| 1.6 | 新增 `JsonProviderAccountRepository.cs`，删除 `JsonModelRegistryRepository.cs` |
| 1.7 | 新增 `ModelEndpointMigrationService.cs` |
| 1.8 | 更新 `model-presets.json`：全量替换 `costHint` → `pricing`，补齐 `group` |
| 1.9 | 更新 `Storage.Json/ServiceCollectionExtensions.cs` DI 注册 |

**此阶段编译会断**（28 个引用点报错），这是预期的，Phase 2 修复。

### Phase 2：Runtime 重构

| 步骤 | 内容 |
|------|------|
| 2.1 | 重命名 `RegistryAwareModelProvider` → `AccountAwareModelProvider`，重写为依赖 `IProviderAccountRepository` |
| 2.2 | 更新 `RuntimeProviderSelector`：`IModelRegistryRepository` → `IProviderAccountRepository` |
| 2.3 | 更新 3 个 Session 服务的构造函数和 `ResolveConfiguredModelAsync` |
| 2.4 | 更新 `BootstrapDraftService` 的模型解析 |
| 2.5 | 更新 `ConfigUpdateTool`：改用 `IProviderAccountRepository` |
| 2.6 | 更新 `ServiceCollectionExtensions.cs`：注册新类型 |
| 2.7 | 删除 `DynamicModelProvider` 中对 `ModelEndpoint` 的引用（如有） |

**验证**：`dotnet build` 0 错 0 警告

### Phase 3：Gateway 重构

| 步骤 | 内容 |
|------|------|
| 3.1 | 新增 `GatewayApp.ProviderAccountEndpoints.cs`，实现全部端点 |
| 3.2 | 删除 `GatewayApp.ModelEndpoints.cs` |
| 3.3 | 更新 `ModelConnectionTestService`：保留 preset-based 测试（Onboarding 用），新增 Account 级测试端点 |
| 3.4 | 删除 `ModelRegistrySeedService.cs`（种子逻辑迁入 ProviderAccountEndpoints） |
| 3.5 | 更新 `GatewayApp.Composition.cs`：DI 注册 + **line 253 中间件**（将 `IModelRegistryRepository` 空状态检查改为 `IProviderAccountRepository`） |
| 3.6 | 更新 `GatewayApp.SetupEndpoints.cs`：Onboarding 创建 Account |
| 3.7 | 更新 `ConfigBootstrapWriter.cs`：创建 Account 而非 Endpoint |
| 3.8 | 更新 `RuntimeConfigurationBootstrap.cs`：私有 `StoredModelEndpoint` record 和 `TryLoadDefaultModelEndpoint()` 方法改为读取新 JSON 格式（`config/accounts/` + `config/account-models/`）；迁移后的文件路径变化需同步 |
| 3.9 | 更新 `ConfigBootstrapService.cs`：注释中引用 `ModelRegistrySeedService`，改为引用新的迁移/种子服务 |
| 3.10 | 更新 `GatewayApp.ModelValidation.cs`、`SessionEndpoints`、`WorkspaceBackupService`、`SecretMigrationReportService`、`ChannelCommandDispatcher` |
| 3.11 | 启动时注册迁移 HostedService |

**验证**：`dotnet build` + `dotnet test KodaClaw.sln -m:1` 全绿

### Phase 4：前端重构

| 步骤 | 内容 |
|------|------|
| 4.1 | `contracts.ts`：删除旧类型，新增 `ProviderAccount`, `AccountModel`, `ModelPricing` 等（**两个前端同步**：`kodaclaw-web` + `kodaclaw-web-v2`） |
| 4.2 | `api.ts`：删除旧函数（`fetchModels`, `createModelEndpoint`, `setDefaultModelEndpoint` 等），新增 `fetchProviderAccounts` 等 |
| 4.3 | `queryKeys.ts`：`models` → `providerAccounts` |
| 4.4 | `App.tsx`：删除旧 API 导入和使用（line 17 导入, line 187 queryFn, line 222 setDefaultModelEndpoint 调用） |
| 4.5 | 重写 `ModelsSettingsDesk.tsx`：两级结构 + 定价展示 |
| 4.6 | 重写 `ModelStep.tsx` Onboarding：`createProviderAccount` + preset group 自动附加 |
| 4.7 | 更新其他引用 `ModelEndpoint` 的组件（SessionDetail 等） |
| 4.8 | `kodaclaw-web-v2/` 所有对应文件镜像修改（6 个文件，见文件清单） |

**验证**：`cd apps/kodaclaw-web && npm run typecheck` + `cd apps/kodaclaw-web-v2 && npm run typecheck` 0 错误

### Phase 5：测试 + Dogfood

| 步骤 | 内容 |
|------|------|
| 5.1 | 新增单元测试：`JsonProviderAccountRepository` CRUD、`ResolveDefaultForAsync` 能力匹配 |
| 5.2 | 新增单元测试：`AccountAwareModelProvider` 路由和缓存 |
| 5.3 | 新增单元测试：`ModelEndpointMigrationService` 各种分组场景 |
| 5.4 | 新增集成测试：Gateway 端点 CRUD + 测试连接 |
| 5.5 | 新增契约测试：ProviderAccount / AccountModel JSON 序列化 |
| 5.6 | **更新 13 个现有测试文件**（见"需更新的测试文件"清单），包括：删除 `JsonModelRegistryRepositoryTests`、`RegistryAwareModelProviderTests`、`ModelApiIntegrationTests`、`ModelRegistrySeedServiceIntegrationTests`；更新其余 9 个文件中的 `ModelEndpoint`/`IModelRegistryRepository` mock |
| 5.7 | 更新前端测试：`models-settings-desk.spec.tsx`（kodaclaw-web + kodaclaw-web-v2） |
| 5.8 | 全量回归：`make test-solution` + `npm run typecheck` |
| 5.9 | Dogfood：全新 workspace + 已有 workspace 迁移 + 多 account 并存 |

---

## 10. 变更文件清单

### 删除的文件

| 文件 | 说明 |
|------|------|
| `src/KodaClaw.Contracts/Models/ModelEndpoint.cs` | 旧 record |
| `src/KodaClaw.Contracts/Models/IModelRegistryRepository.cs` | 旧接口 |
| `src/KodaClaw.Contracts/Models/CreateModelEndpointRequest.cs` | 旧 DTO |
| `src/KodaClaw.Contracts/Models/UpdateModelEndpointRequest.cs` | 旧 DTO |
| `src/KodaClaw.Contracts/Models/ModelsQueryResponse.cs` | 旧 DTO |
| `src/KodaClaw.Storage.Json/Repositories/JsonModelRegistryRepository.cs` | 旧仓储 |
| `src/KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs` | 旧端点 |
| `src/KodaClaw.Gateway/Models/ModelRegistrySeedService.cs` | 旧种子服务 |

### 新增的文件

| 文件 | 说明 |
|------|------|
| `src/KodaClaw.Contracts/Models/ProviderAccount.cs` | Account record |
| `src/KodaClaw.Contracts/Models/AccountModel.cs` | Model record |
| `src/KodaClaw.Contracts/Models/ModelPricing.cs` | 定价 record |
| `src/KodaClaw.Contracts/Models/ResolvedModel.cs` | 解析结果 record |
| `src/KodaClaw.Contracts/Models/IProviderAccountRepository.cs` | 新接口 |
| `src/KodaClaw.Contracts/Models/ProviderAccountContracts.cs` | 请求/响应 DTO |
| `src/KodaClaw.Storage.Json/Repositories/JsonProviderAccountRepository.cs` | 新 JSON 仓储 |
| `src/KodaClaw.Storage.Json/Migration/ModelEndpointMigrationService.cs` | 迁移服务 |
| `src/KodaClaw.Runtime/Providers/AccountAwareModelProvider.cs` | 新 provider 路由 |
| `src/KodaClaw.Gateway/Endpoints/GatewayApp.ProviderAccountEndpoints.cs` | 新 REST 端点 |

### 修改的文件（源码）

| 文件 | 说明 |
|------|------|
| `src/KodaClaw.Contracts/Models/ModelPreset.cs` | 删 CostHint，加 Group + Pricing |
| `src/KodaClaw.Contracts/Sessions/SessionDetail.cs` | ModelEndpoint → AccountModel 引用 |
| `src/KodaClaw.Runtime/Providers/RuntimeProviderSelection.cs` | IModelRegistryRepository → IProviderAccountRepository |
| `src/KodaClaw.Runtime/Providers/RegistryAwareModelProvider.cs` | **删除**（被 AccountAwareModelProvider 替代） |
| `src/KodaClaw.Runtime/ServiceCollectionExtensions.cs` | DI 注册新类型 |
| `src/KodaClaw.Runtime/Sessions/MainSessionService.cs` | 构造函数 + 模型解析 |
| `src/KodaClaw.Runtime/Sessions/ChannelSessionService.cs` | 同上 |
| `src/KodaClaw.Runtime/Sessions/AutomationSessionService.cs` | 同上 |
| `src/KodaClaw.Runtime/Bootstrap/BootstrapDraftService.cs` | 模型解析 |
| `src/KodaClaw.Runtime/Tools/ConfigUpdateTool.cs` | 改用新接口 |
| `src/KodaClaw.Gateway/Composition/GatewayApp.Composition.cs` | DI 注册 + 中间件（line 253 检查 registry 空状态的逻辑需改为检查 account repo） |
| `src/KodaClaw.Gateway/Bootstrap/ConfigBootstrapWriter.cs` | 创建 Account |
| `src/KodaClaw.Gateway/Bootstrap/RuntimeConfigurationBootstrap.cs` | **特殊处理**：直接反序列化旧 `ModelEndpoint` JSON（`StoredModelEndpoint` 私有 record），需改为反序列化新 `ProviderAccount` + `AccountModel` |
| `src/KodaClaw.Gateway/Bootstrap/ConfigBootstrapService.cs` | 注释引用 `ModelRegistrySeedService`，需更新注释和启动顺序 |
| `src/KodaClaw.Gateway/Validation/GatewayApp.ModelValidation.cs` | Account 级校验 |
| `src/KodaClaw.Gateway/Models/ModelConnectionTestService.cs` | Account 级测试 |
| `src/KodaClaw.Gateway/Models/ModelPresetService.cs` | 加载 group + pricing |
| `src/KodaClaw.Gateway/Endpoints/GatewayApp.SetupEndpoints.cs` | Onboarding 适配 |
| `src/KodaClaw.Gateway/Endpoints/GatewayApp.SessionEndpoints.cs` | 新类型引用 |
| `src/KodaClaw.Gateway/Workspace/WorkspaceBackupService.cs` | 新类型引用 |
| `src/KodaClaw.Gateway/Diagnostics/SecretMigrationReportService.cs` | 新类型引用 |
| `src/KodaClaw.Gateway/Resources/model-presets.json` | costHint → pricing，加 group（共 25 个 preset） |
| `src/KodaClaw.ChannelHub/Commands/ChannelCommandDispatcher.cs` | 新类型引用 |
| `src/KodaClaw.Storage.Json/ServiceCollectionExtensions.cs` | DI 注册 |

### 修改的文件（前端 kodaclaw-web）

| 文件 | 说明 |
|------|------|
| `apps/kodaclaw-web/src/types/contracts.ts` | 全部替换 |
| `apps/kodaclaw-web/src/lib/api.ts` | 全部替换（删除 `fetchModels`, `setDefaultModelEndpoint` 等旧函数） |
| `apps/kodaclaw-web/src/lib/queryKeys.ts` | `models` query key 重命名为 `providerAccounts` |
| `apps/kodaclaw-web/src/App.tsx` | 删除 `fetchModels`/`setDefaultModelEndpoint` 导入（line 17）和使用（lines 187, 222），改用新 API |
| `apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx` | **重写** |
| `apps/kodaclaw-web/src/onboarding/steps/ModelStep.tsx` | **重写** |

### 修改的文件（前端 kodaclaw-web-v2，镜像修改）

> `apps/kodaclaw-web-v2/` 是 web 前端的 v2 重构版本，包含相同的模型管理代码，需同步修改。

| 文件 | 说明 |
|------|------|
| `apps/kodaclaw-web-v2/src/types/contracts.ts` | 同 kodaclaw-web，全部替换 |
| `apps/kodaclaw-web-v2/src/lib/api.ts` | 同 kodaclaw-web，全部替换 |
| `apps/kodaclaw-web-v2/src/App.tsx` | 同 kodaclaw-web，删除旧 API 引用 |
| `apps/kodaclaw-web-v2/src/components/ModelsSettingsDesk.tsx` | **重写** |
| `apps/kodaclaw-web-v2/src/onboarding/steps/ModelStep.tsx` | **重写** |
| `apps/kodaclaw-web-v2/src/__tests__/models-settings-desk.spec.tsx` | 适配新类型 |

### 需更新的测试文件（13 个）

| 文件 | 说明 |
|------|------|
| `tests/KodaClaw.UnitTests/Gateway/ConfigBootstrapWriterTests.cs` | `ModelEndpoint` mock → `ProviderAccount` + `AccountModel` |
| `tests/KodaClaw.UnitTests/ModelHub/JsonModelRegistryRepositoryTests.cs` | **删除**，替换为 `JsonProviderAccountRepositoryTests` |
| `tests/KodaClaw.UnitTests/Runtime/RegistryAwareModelProviderTests.cs` | **删除**，替换为 `AccountAwareModelProviderTests` |
| `tests/KodaClaw.UnitTests/ChannelHub/ChannelCommandWave5Tests.cs` | `ModelEndpoint` mock 替换 |
| `tests/KodaClaw.UnitTests/Runtime/ChannelSessionServiceModelTests.cs` | `IModelRegistryRepository` mock 替换 |
| `tests/KodaClaw.ContractTests/Models/ModelContractsTests.cs` | 旧 DTO 序列化测试 → 新 DTO |
| `tests/KodaClaw.IntegrationTests/Gateway/ModelApiIntegrationTests.cs` | **删除**，替换为 `ProviderAccountApiIntegrationTests` |
| `tests/KodaClaw.IntegrationTests/Gateway/ModelRegistrySeedServiceIntegrationTests.cs` | **删除**（种子服务被移除） |
| `tests/KodaClaw.IntegrationTests/Gateway/SecretMigrationReportIntegrationTests.cs` | `ModelEndpoint` → 新类型 |
| `tests/KodaClaw.IntegrationTests/Gateway/BackupApiIntegrationTests.cs` | 备份逻辑涉及 `ModelEndpoint` |
| `tests/KodaClaw.IntegrationTests/Gateway/ModelRuntimeBootstrapIntegrationTests.cs` | 启动 bootstrap 涉及旧类型 |
| `tests/KodaClaw.IntegrationTests/Gateway/SetupWizardIntegrationTests.cs` | Onboarding 创建 endpoint → account |
| `tests/KodaClaw.IntegrationTests/Smoke/Iteration7AcceptanceIntegrationTests.cs` | 可能引用旧 API 路径 |
| `apps/kodaclaw-web/src/__tests__/models-settings-desk.spec.tsx` | 前端组件测试，适配新类型 |

---

## 11. 审查补遗（2026-04-13 全量代码审查）

对方案进行了 6 路并行代码审查（Contracts / Storage+Runtime / Gateway / Frontend / Tests / Presets），发现以下问题已全部修正：

### 11.1 新发现的引用文件

| # | 文件 | 发现内容 | 处置 |
|---|------|---------|------|
| 1 | `Gateway/Bootstrap/ConfigBootstrapService.cs` | 注释引用 `ModelRegistrySeedService` 启动顺序 | 已加入 Phase 3.9 |
| 2 | `Gateway/Bootstrap/RuntimeConfigurationBootstrap.cs` | 私有 `StoredModelEndpoint` record + `TryLoadDefaultModelEndpoint()` 直接反序列化旧 JSON | 已加入 Phase 3.8，需改为读取新格式 |
| 3 | `Gateway/Composition/GatewayApp.Composition.cs` line 253 | 中间件检查 `IModelRegistryRepository` 是否为空 | 已加入 Phase 3.5 |
| 4 | `apps/kodaclaw-web/src/App.tsx` | 导入并使用 `fetchModels`/`setDefaultModelEndpoint`（line 17, 187, 222） | 已加入 Phase 4.4 |
| 5 | `apps/kodaclaw-web/src/lib/queryKeys.ts` | `models` query key | 已加入 Phase 4.3 |

### 11.2 遗漏的前端目录

`apps/kodaclaw-web-v2/` 是 web 前端的 v2 重构版（使用 shadcn/ui），包含 6 个文件引用 `ModelEndpoint`/旧 API。已补充到文件清单和 Phase 4.8。

### 11.3 测试文件清单

原方案仅提及"新增测试"，遗漏了 **13 个现有测试文件**需更新或删除。已补充到文件清单和 Phase 5.6-5.7。

### 11.4 Preset 数据修正

- 实际 preset 数量为 **25 个**（非 26 个）
- Anthropic 系 preset 的 `costHint` 仅含 input 价格（如 `"$3/M input"`），转换时需补齐 output 价格：
  - Sonnet 4.6: $3/$15、Opus 4.6: $5/$25、Haiku 4.5: $1/$5（参考 Anthropic 官方定价）
- DeepSeek `costHint` 格式为 `"¥2/M input"`，需补齐 output：V3 ¥2/¥8、R1 ¥4/¥16

### 11.5 保留的 Contracts

`ModelConnectionTestRequest` 和 `ModelConnectionTestResponse` **保留不变**——Onboarding 连通性测试基于 preset + 临时凭证，不依赖已持久化的 Account，因此这两个 DTO 仍有用。已加入 §3.3 保留类型表。

### 11.6 数据统计

| 指标 | 数量 |
|------|------|
| 待删除文件 | 8 |
| 待新增文件 | 10 |
| 待修改源码文件 | 24 |
| 待修改前端文件（kodaclaw-web） | 6 |
| 待修改前端文件（kodaclaw-web-v2） | 6 |
| 待更新/删除测试文件 | 14（含 1 个前端测试） |
| **总受影响文件** | **68** |

---

## 12. 非目标（OUT OF SCOPE）

- Session 内自适应模型切换（同 account 按场景自动升降级）
- 模型用量统计（按 account/model 维度的 token 消耗）
- Account 共享/团队权限
- Provider 健康检查 + 自动 fallback
- SDK 层改动（`IModelProvider` 接口不变）
- 定价自动拉取（部分厂商有 API，未来可做）

---

## 13. 验证命令

```bash
# ─── L0：编译 ─────────────────────────────────────
dotnet build KodaClaw.sln
cd apps/kodaclaw-web && npm run typecheck

# ─── L1：单元测试 ─────────────────────────────────
dotnet test tests/KodaClaw.UnitTests/KodaClaw.UnitTests.csproj \
  --filter "FullyQualifiedName~ProviderAccount"

# ─── L2：集成测试 ─────────────────────────────────
dotnet test tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ProviderAccount"

# ─── L3：契约测试 ─────────────────────────────────
dotnet test tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj \
  --filter "FullyQualifiedName~ProviderAccount"

# ─── 全量回归 ─────────────────────────────────────
make test-solution

# ─── L5：Dogfood ──────────────────────────────────
# 1. 全新 workspace
WORKSPACE_ROOT=~/.kodaclaw-test make run-gateway
# Onboarding → 选 Anthropic → 验证自动创建 account + 3 models + 定价

# 2. 已有 workspace（迁移验证）
make run-gateway
# 日志应输出 "Migrated N endpoints to M accounts"
# Settings → 模型管理 → 验证旧 endpoint 已合并为 account

# 3. 多 account 并存 + Coding Plan
# 添加 Anthropic + 智谱标准 API + 智谱 Coding Plan
# 验证定价展示、默认切换、account 禁用
```

---

## 14. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| 迁移丢数据 | 高 | 旧目录备份为 `config/models.migrated/`；旧 secret 复制不删除 |
| 迁移中 Secret 引用格式变化 | 中 | 迁移时 `keychain:models:old-id` → `keychain:accounts:new-id`，值原子复制 |
| Coding Plan accessMode 推断不准 | 中 | 基于 BaseUrl 模式匹配（已知的 3 个国内端点），未知端点默认 `"api"` |
| Phase 1 打断编译 | 低 | 预期行为，Phase 2 修复；可在分支上一次性完成 Phase 1+2 |
| 测试大面积修改 | 中 | Phase 5 专门处理；旧测试中 `ModelEndpoint` mock 全部改为 `ProviderAccount` + `AccountModel` |
| Desktop 壳对旧 API 的依赖 | 低 | Desktop 通过 Web 交互，不直接调 REST API；若有直调需同步改 |

---

## 15. 关键设计决策

### 决策 1：Account 按"协议+凭证"分组，不按厂商名称

同一厂商的标准 API 和 Coding Plan 是两个独立 Account。
**原因**：协议不同、Key 不同、BaseUrl 不同。

### 决策 2：彻底删除 ModelEndpoint，不保留兼容层

**原因**：
- 保留旧类型 = 维护两套并行数据模型，长期负担大于短期收益
- 所有消费方（Runtime / Gateway / Frontend）一次性切换，不留灰色地带
- 迁移服务处理旧数据，迁移完成后旧类型无存在意义

### 决策 3：CostHint 直接删除，不做 fallback

**原因**：
- `model-presets.json` 是内置文件，可以一次性全量改完
- 用户创建的 endpoint 通过迁移生成的 AccountModel 没有 CostHint（本来也没有）
- 保留 fallback 会让前端格式化逻辑永远有两条分支

### 决策 4：迁移在应用启动时自动执行

**原因**：
- 用户无需手动操作
- 通过 `config/models/` 目录存在性判断
- 迁移失败不阻塞启动（DynamicModelProvider 环境变量 fallback 仍工作）

### 决策 5：Onboarding 选一个 preset 自动附加同组所有模型

**原因**：
- 通过 preset 的 `group` 字段确定同组关系
- 用户添加一次 API Key，自动获得该 provider 的所有模型
- 降低新用户配置门槛
