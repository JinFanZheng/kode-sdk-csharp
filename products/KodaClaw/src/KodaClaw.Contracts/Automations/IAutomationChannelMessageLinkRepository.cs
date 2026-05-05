using KodaClaw.Contracts.Channels;

namespace KodaClaw.Contracts.Automations;

public interface IAutomationChannelMessageLinkRepository
{
    Task UpsertAsync(
        AutomationChannelMessageLink link,
        CancellationToken cancellationToken = default);

    Task<AutomationChannelMessageLink?> GetByExternalMessageAsync(
        ChannelConnectorKind connectorKind,
        string accountId,
        string externalThreadId,
        string externalMessageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationChannelMessageLink>> ListByRunIdAsync(
        string runId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

