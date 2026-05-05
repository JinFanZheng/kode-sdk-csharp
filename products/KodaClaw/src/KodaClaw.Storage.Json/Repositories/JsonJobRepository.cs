using System.Security.Cryptography;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

public sealed class JsonJobRepository : JsonStoreBase, IJobRepository
{
    private readonly string _jobsDir;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonJobRepository(string workspaceRoot)
    {
        _jobsDir = Path.Combine(workspaceRoot, "jobs");
        _indexPath = Path.Combine(_jobsDir, "index.json");
        EnsureDirectory(_indexPath);
        CleanupTempFiles();
    }

    public async Task<JobDefinition> CreateAsync(JobDefinition job, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = "job-" + RandomNumberGenerator.GetHexString(12);

        var created = job with
        {
            Id = id,
            Status = JobStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyKey = string.IsNullOrWhiteSpace(job.ConcurrencyKey) ? id : job.ConcurrencyKey,
            Runs = [],
            RunCount = 0,
        };

        await _lock.WaitAsync(ct);
        try
        {
            var filePath = JobFilePath(id);
            await WriteEntityAsync(filePath, created, ct);
            await UpsertIndexEntryAsync(created, ct);
            return created;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JobDefinition?> GetByIdAsync(string jobId, CancellationToken ct)
    {
        var filePath = JobFilePath(jobId);
        return await ReadEntityAsync<JobDefinition>(filePath, ct);
    }

    public async Task<IReadOnlyList<JobDefinition>> ListAsync(JobStatus? statusFilter, CancellationToken ct)
    {
        var jobs = await ScanDirectoryAsync<JobDefinition>(_jobsDir, j =>
            statusFilter == null || j.Status == statusFilter.Value, ct);
        return jobs;
    }

    public async Task UpdateAsync(JobDefinition job, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = job with { UpdatedAt = now };

        await _lock.WaitAsync(ct);
        try
        {
            var filePath = JobFilePath(job.Id);
            await WriteEntityAsync(filePath, updated, ct);
            await UpsertIndexEntryAsync(updated, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string jobId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = JobFilePath(jobId);
            if (File.Exists(filePath))
                File.Delete(filePath);
            await RemoveFromIndexAsync(jobId, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private string JobFilePath(string jobId) => Path.Combine(_jobsDir, $"{jobId}.json");

    private void CleanupTempFiles()
    {
        if (!Directory.Exists(_jobsDir)) return;
        foreach (var tmp in Directory.GetFiles(_jobsDir, "*.tmp"))
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private async Task UpsertIndexEntryAsync(JobDefinition job, CancellationToken ct)
    {
        var index = await ReadEntityAsync<JobIndex>(_indexPath, ct) ?? new JobIndex(1, []);
        index.Jobs[job.Id] = new JobIndexEntry(
            job.Name,
            job.Type.ToString(),
            job.Status.ToString(),
            job.NextRunAt?.ToString("O"));
        await WriteEntityAsync(_indexPath, index, ct);
    }

    private async Task RemoveFromIndexAsync(string jobId, CancellationToken ct)
    {
        var index = await ReadEntityAsync<JobIndex>(_indexPath, ct);
        if (index == null) return;
        if (!index.Jobs.Remove(jobId)) return;
        await WriteEntityAsync(_indexPath, index, ct);
    }

    private sealed record JobIndex(int Version, Dictionary<string, JobIndexEntry> Jobs);
}
