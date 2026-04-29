namespace KodaClaw.Contracts.Models;

/// <summary>
/// Repository for <see cref="ProviderAccount"/> and <see cref="AccountModel"/> persistence.
/// Replaces the former <c>IModelRegistryRepository</c>.
/// </summary>
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

    // ── Default management + capability matching ─────────────
    Task<bool> SetGlobalDefaultAsync(string modelId, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the first enabled model whose capabilities include all <paramref name="required"/>
    /// flags, paired with its owning account. Global-default models are preferred.
    /// Returns <c>null</c> when no match is found.
    /// </summary>
    Task<ResolvedModel?> ResolveDefaultForAsync(
        ModelCapabilitySet required, CancellationToken ct = default);
}
