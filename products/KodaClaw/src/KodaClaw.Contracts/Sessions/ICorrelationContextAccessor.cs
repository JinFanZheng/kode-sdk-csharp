namespace KodaClaw.Contracts.Sessions;

public interface ICorrelationContextAccessor
{
    string? CorrelationId { get; set; }
}
