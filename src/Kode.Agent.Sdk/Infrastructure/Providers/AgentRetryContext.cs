namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// Internal AsyncLocal context for SDK-level retry notifications.
/// <para>
/// <see cref="Agent.Agent"/> sets <see cref="Current"/> in
/// <c>StreamModelResponseAsync</c> before calling the model provider.
/// <see cref="ProviderRetryHelper"/> reads it in <see cref="ProviderRetryHelper.InvokeOnRetry"/>
/// so that <c>ModelRetryingEvent</c> is emitted on the agent's EventBus automatically —
/// without any wiring required in host applications.
/// </para>
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so concurrent agent sessions are fully isolated.
/// </para>
/// </summary>
internal static class AgentRetryContext
{
    internal static readonly AsyncLocal<Action<RetryAttemptContext>?> Current = new();
}
