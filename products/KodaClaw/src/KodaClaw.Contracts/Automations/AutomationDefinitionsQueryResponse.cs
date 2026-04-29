namespace KodaClaw.Contracts.Automations;

public sealed record AutomationDefinitionsQueryResponse(
    IReadOnlyList<AutomationDefinition> Items);
