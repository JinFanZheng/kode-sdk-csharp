using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

public sealed class JsonAutomationChannelMessageLinkRepository : JsonStoreBase, IAutomationChannelMessageLinkRepository
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<(string, string, string, string), string> _index = new();
    private volatile bool _loaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public JsonAutomationChannelMessageLinkRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "automation-channel-links");
    }

    public async Task UpsertAsync(
        AutomationChannelMessageLink link,
        CancellationToken cancellationToken = default)
    {
        await WriteEntityAsync(FilePath(link.Id), link, cancellationToken);
        _index[IndexKey(link)] = link.Id;
    }

    public async Task<AutomationChannelMessageLink?> GetByExternalMessageAsync(
        ChannelConnectorKind connectorKind,
        string accountId,
        string externalThreadId,
        string externalMessageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var key = (connectorKind.ToString(), accountId, externalThreadId, externalMessageId);
        if (!_index.TryGetValue(key, out var id))
        {
            return null;
        }

        return await ReadEntityAsync<AutomationChannelMessageLink>(FilePath(id), cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationChannelMessageLink>> ListByRunIdAsync(
        string runId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<AutomationChannelMessageLink>(
            _dir,
            link => string.Equals(link.RunId, runId, StringComparison.Ordinal),
            cancellationToken);

        return all
            .OrderByDescending(static link => link.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            var all = await ScanDirectoryAsync<AutomationChannelMessageLink>(_dir, null, cancellationToken);
            foreach (var link in all)
            {
                _index[IndexKey(link)] = link.Id;
            }

            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    private static (string, string, string, string) IndexKey(AutomationChannelMessageLink link)
        => (
            link.ConnectorKind.ToString(),
            link.AccountId,
            link.ExternalThreadId,
            link.ExternalMessageId);

    public static string BuildId(
        ChannelConnectorKind connectorKind,
        string accountId,
        string externalThreadId,
        string externalMessageId)
    {
        var raw = string.Join("::", connectorKind, accountId, externalThreadId, externalMessageId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"automation-channel-link-{Convert.ToHexString(bytes[..16]).ToLowerInvariant()}";
    }
}

