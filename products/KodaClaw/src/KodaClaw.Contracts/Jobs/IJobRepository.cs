namespace KodaClaw.Contracts.Jobs;

public interface IJobRepository
{
    /// <summary>
    /// 创建 Job。id 自动生成，created_at/updated_at 自动写入。
    /// 返回写入后的完整 JobDefinition。
    /// </summary>
    Task<JobDefinition> CreateAsync(JobDefinition job, CancellationToken ct);

    /// <summary>
    /// 按 ID 读取单个 Job。不存在返回 null。
    /// </summary>
    Task<JobDefinition?> GetByIdAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// 列出所有 Job，可选按 status 过滤。
    /// </summary>
    Task<IReadOnlyList<JobDefinition>> ListAsync(JobStatus? statusFilter, CancellationToken ct);

    /// <summary>
    /// 更新 Job（全量替换）。调用方负责保证 id 不变。
    /// updated_at 自动更新。
    /// </summary>
    Task UpdateAsync(JobDefinition job, CancellationToken ct);

    /// <summary>
    /// 删除 Job。不存在则静默成功。
    /// </summary>
    Task DeleteAsync(string jobId, CancellationToken ct);
}
