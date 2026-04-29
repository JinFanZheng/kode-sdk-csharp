using System.Threading;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ControlPlane;

public sealed class AsyncLocalCorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<string?> CurrentCorrelationId = new();

    public string? CorrelationId
    {
        get => CurrentCorrelationId.Value;
        set => CurrentCorrelationId.Value = value;
    }
}
