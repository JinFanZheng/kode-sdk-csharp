namespace KodaClaw.Runtime.Bootstrap;

public sealed class BootstrapDraftOptions
{
    public string Model { get; init; } = "koda-main";

    public string? SystemPrompt { get; init; } = "You are KodaClaw bootstrap assistant.";

    public int? MaxTokens { get; init; } = 1200;

    public double? Temperature { get; init; } = 0.2;
}
