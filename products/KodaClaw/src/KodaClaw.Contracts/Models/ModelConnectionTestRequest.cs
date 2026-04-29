namespace KodaClaw.Contracts.Models;

public sealed record ModelConnectionTestRequest(
    string? PresetId,
    string? ModelId,
    string? BaseUrl,
    string ApiKey,
    string? Provider = null,
    string? EndpointId = null
);
