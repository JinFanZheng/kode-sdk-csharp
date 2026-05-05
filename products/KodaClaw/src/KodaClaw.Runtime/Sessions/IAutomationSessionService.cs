using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime.Sessions;

public interface IAutomationSessionService
{
    Task<AutomationSessionHandle> StartAutomationSessionAsync(
        AutomationDefinition definition,
        CancellationToken cancellationToken = default);

    Task<AutomationSessionHandle> StartJobSessionAsync(
        JobDefinition job,
        CancellationToken cancellationToken = default);

    Task<AutomationFollowUpResult> RunFollowUpAsync(
        AutomationFollowUpRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AutomationFollowUpResult(
            Success: false,
            Response: null,
            ErrorMessage: "Automation follow-up is not supported by this session service.",
            SessionId: request.Link.SessionId));
}

public sealed record AutomationSessionHandle(
    string SessionId,
    string AutomationId,
    SessionKind SessionKind,
    string SessionDirectory,
    IAgent Agent);

public sealed record AutomationFollowUpRequest(
    AutomationChannelMessageLink Link,
    ThreadBinding Binding,
    ChannelEventEnvelope Envelope,
    string Text);

public sealed record AutomationFollowUpResult(
    bool Success,
    string? Response,
    string? ErrorMessage,
    string SessionId);
