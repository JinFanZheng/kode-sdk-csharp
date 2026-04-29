namespace KodaClaw.Contracts.Automations;

public sealed record TriggerAutomationResponse(
    bool Ok,
    string RunId);
