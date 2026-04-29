using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// JSON file storage for <see cref="ProviderAccount"/> and <see cref="AccountModel"/>.
/// Accounts: {workspaceRoot}/config/accounts/{id}.json
/// Models:   {workspaceRoot}/config/account-models/{id}.json
/// </summary>
public sealed class JsonProviderAccountRepository : JsonStoreBase, IProviderAccountRepository
{
    private readonly string _accountsDir;
    private readonly string _modelsDir;

    public JsonProviderAccountRepository(string workspaceRoot)
    {
        _accountsDir = Path.Combine(workspaceRoot, "config", "accounts");
        _modelsDir = Path.Combine(workspaceRoot, "config", "account-models");
    }

    // ── Account CRUD ─────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        var accounts = await ScanDirectoryAsync<ProviderAccount>(_accountsDir, null, ct);
        return accounts.OrderBy(a => a.CreatedAt).ToList();
    }

    public Task<ProviderAccount?> GetAccountByIdAsync(string id, CancellationToken ct = default)
        => ReadEntityAsync<ProviderAccount>(AccountFilePath(id), ct);

    public Task AddAccountAsync(ProviderAccount account, CancellationToken ct = default)
        => WriteEntityAsync(AccountFilePath(account.Id), account, ct);

    public async Task<bool> UpdateAccountAsync(ProviderAccount account, CancellationToken ct = default)
    {
        if (!File.Exists(AccountFilePath(account.Id))) return false;
        await WriteEntityAsync(AccountFilePath(account.Id), account, ct);
        return true;
    }

    public async Task<bool> DeleteAccountAsync(string id, CancellationToken ct = default)
    {
        // Cascade delete all models under this account
        var models = await ListModelsAsync(id, ct);
        foreach (var m in models)
            await DeleteEntityAsync(ModelFilePath(m.Id), ct);
        await DeleteEntityAsync(AccountFilePath(id), ct);
        return true;
    }

    // ── AccountModel CRUD ────────────────────────────────────

    public async Task<IReadOnlyList<AccountModel>> ListModelsAsync(string accountId, CancellationToken ct = default)
    {
        var allModels = await ScanDirectoryAsync<AccountModel>(_modelsDir, null, ct);
        return allModels
            .Where(m => m.AccountId == accountId)
            .OrderBy(m => m.CreatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<AccountModel>> ListAllModelsAsync(CancellationToken ct = default)
    {
        var models = await ScanDirectoryAsync<AccountModel>(_modelsDir, null, ct);
        return models.OrderBy(m => m.CreatedAt).ToList();
    }

    public Task<AccountModel?> GetModelByIdAsync(string modelId, CancellationToken ct = default)
        => ReadEntityAsync<AccountModel>(ModelFilePath(modelId), ct);

    public Task AddModelAsync(AccountModel model, CancellationToken ct = default)
        => WriteEntityAsync(ModelFilePath(model.Id), model, ct);

    public async Task<bool> UpdateModelAsync(AccountModel model, CancellationToken ct = default)
    {
        if (!File.Exists(ModelFilePath(model.Id))) return false;
        await WriteEntityAsync(ModelFilePath(model.Id), model, ct);
        return true;
    }

    public async Task<bool> DeleteModelAsync(string modelId, CancellationToken ct = default)
    {
        await DeleteEntityAsync(ModelFilePath(modelId), ct);
        return true;
    }

    // ── Default management + capability matching ─────────────

    public async Task<bool> SetGlobalDefaultAsync(string modelId, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var allModels = await ScanDirectoryAsync<AccountModel>(_modelsDir, null, ct);
        if (!allModels.Any(m => m.Id == modelId)) return false;

        foreach (var m in allModels)
        {
            var updated = m with
            {
                IsGlobalDefault = m.Id == modelId,
                UpdatedAt = m.Id == modelId ? updatedAt : m.UpdatedAt,
            };
            await WriteEntityAsync(ModelFilePath(m.Id), updated, ct);
        }
        return true;
    }

    public async Task<ResolvedModel?> ResolveDefaultForAsync(
        ModelCapabilitySet required, CancellationToken ct = default)
    {
        var accounts = await ScanDirectoryAsync<ProviderAccount>(_accountsDir, null, ct);
        var allModels = await ScanDirectoryAsync<AccountModel>(_modelsDir, null, ct);

        var enabledAccounts = accounts
            .Where(a => a.Enabled)
            .ToDictionary(a => a.Id);

        return allModels
            .Where(m => m.Enabled
                && enabledAccounts.ContainsKey(m.AccountId)
                && (m.Capabilities & required) == required)
            .OrderByDescending(m => m.IsGlobalDefault)
            .ThenBy(m => m.CreatedAt)
            .Select(m => new ResolvedModel(enabledAccounts[m.AccountId], m))
            .FirstOrDefault();
    }

    // ─── Private helpers ─────────────────────────────────────

    private string AccountFilePath(string id) => Path.Combine(_accountsDir, $"{id}.json");
    private string ModelFilePath(string id) => Path.Combine(_modelsDir, $"{id}.json");
}
