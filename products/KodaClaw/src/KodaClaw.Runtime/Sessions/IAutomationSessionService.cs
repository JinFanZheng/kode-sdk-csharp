using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime.Sessions;

public interface IAutomationSessionService
{
    Task<AutomationSessionHandle> StartAutomationSessionAsync(
        AutomationDefinition definition,
        CancellationToken cancellationToken = default);
}

public sealed record AutomationSessionHandle(
    string SessionId,
    string AutomationId,
    SessionKind SessionKind,
    string SessionDirectory,
    IAgent Agent);
