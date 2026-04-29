namespace KodaClaw.Contracts.Channels;

public interface IThreadBindingRepository
{
    Task UpsertAsync(ThreadBinding binding, CancellationToken cancellationToken = default);

    Task<ThreadBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<ThreadBinding?> GetByExternalThreadAsync(
        ChannelConnectorKind connectorKind,
        string accountId,
        string externalThreadId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreadBinding>> ListAsync(
        ChannelQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>按 SessionId 查找 binding。用于 GetSessionModelAsync 的重启后回查路径。</summary>
    Task<ThreadBinding?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>删除指定账号下的所有线程绑定，返回删除数量。</summary>
    Task<int> DeleteByAccountIdAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>批量更新指定账号下所有线程绑定的投递模式，返回更新数量。</summary>
    Task<int> UpdateDeliveryModeByAccountIdAsync(string accountId, DeliveryMode mode, CancellationToken cancellationToken = default);

    Task<bool> UpdateDeliveryModeOverrideAsync(
        string id,
        DeliveryMode? deliveryModeOverride,
        CancellationToken cancellationToken = default);
}
