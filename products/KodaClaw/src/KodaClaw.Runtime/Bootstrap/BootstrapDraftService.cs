using System.Text;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.ModelHub;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.Runtime;

public sealed class BootstrapDraftService : IBootstrapDraftService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IModelProvider _modelProvider;
    private readonly BootstrapDraftOptions _options;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IProviderAccountRepository? _accountRepository;

    public BootstrapDraftService(
        IModelProvider modelProvider,
        BootstrapDraftOptions? options = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IProviderAccountRepository? accountRepository = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _options = options ?? new BootstrapDraftOptions();
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _accountRepository = accountRepository;
    }

    public async Task<BootstrapDraftResult> GenerateDraftAsync(
        BootstrapDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transcript = BuildTranscript(request.Conversation);
        var hasSeedDrafts =
            !string.IsNullOrWhiteSpace(request.IdentityMarkdown) ||
            !string.IsNullOrWhiteSpace(request.SoulMarkdown) ||
            !string.IsNullOrWhiteSpace(request.UserMarkdown);
        if (string.IsNullOrWhiteSpace(transcript) && !hasSeedDrafts)
        {
            throw new ArgumentException("Bootstrap draft generation requires conversation evidence or current draft content.", nameof(request));
        }

        var prompt = new PromptBuilder(PromptProfiles.Bootstrap(_options.SystemPrompt))
            .AddSection(
                "Draft Objective",
                [
                    "Turn onboarding conversation into durable markdown files for IDENTITY.md, SOUL.md, and USER.md.",
                    "Prefer conservative drafts. If a detail is uncertain, use a placeholder or neutral wording instead of inventing facts.",
                    "Return JSON only.",
                ])
            .AddSection(
                "Output JSON Contract",
                "{\n  \"identityMarkdown\": \"...\",\n  \"soulMarkdown\": \"...\",\n  \"userMarkdown\": \"...\",\n  \"summary\": \"short explanation of what was inferred and where assumptions remain\"\n}")
            .AddSection(
                "Current Drafts",
                [
                    "### IDENTITY.md",
                    string.IsNullOrWhiteSpace(request.IdentityMarkdown) ? "(none)" : request.IdentityMarkdown!.Trim(),
                    string.Empty,
                    "### SOUL.md",
                    string.IsNullOrWhiteSpace(request.SoulMarkdown) ? "(none)" : request.SoulMarkdown!.Trim(),
                    string.Empty,
                    "### USER.md",
                    string.IsNullOrWhiteSpace(request.UserMarkdown) ? "(none)" : request.UserMarkdown!.Trim(),
                ])
            .AddSection("Conversation Transcript", string.IsNullOrWhiteSpace(transcript) ? "(none)" : transcript)
            .Build();

        var response = await _modelProvider.CompleteAsync(
            new ModelRequest
            {
                Model = await ResolveConfiguredModelAsync(cancellationToken),
                SystemPrompt = prompt.SystemPrompt,
                Messages =
                [
                    Message.User("Generate the bootstrap draft now. Return JSON only with the required keys.")
                ],
                MaxTokens = _options.MaxTokens,
                Temperature = _options.Temperature,
            },
            cancellationToken);

        var rawText = string.Concat(response.Content.OfType<TextContent>().Select(content => content.Text));
        var payload = DeserializePayload(rawText);

        if (string.IsNullOrWhiteSpace(payload.IdentityMarkdown) ||
            string.IsNullOrWhiteSpace(payload.SoulMarkdown) ||
            string.IsNullOrWhiteSpace(payload.UserMarkdown))
        {
            throw new InvalidOperationException("Bootstrap draft response did not include all required markdown fields.");
        }

        return new BootstrapDraftResult(
            payload.IdentityMarkdown.Trim(),
            payload.SoulMarkdown.Trim(),
            payload.UserMarkdown.Trim(),
            string.IsNullOrWhiteSpace(payload.Summary)
                ? "Draft generated from onboarding conversation."
                : payload.Summary.Trim());
    }

    private Task<string> ResolveConfiguredModelAsync(CancellationToken cancellationToken) =>
        RuntimeProviderSelector.ResolveModelOrFallbackAsync(
            _runtimeConfigurationResolver,
            _options.Model,
            _accountRepository,
            cancellationToken);

    private static string BuildTranscript(IReadOnlyList<BootstrapDraftMessage>? conversation)
    {
        if (conversation is not { Count: > 0 })
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var message in conversation)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var normalizedRole = NormalizeRole(message.Role);
            builder.AppendLine($"[{normalizedRole}] {message.Text.Trim()}");
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeRole(string? role)
    {
        return role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user",
        };
    }

    private static BootstrapDraftPayload DeserializePayload(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new InvalidOperationException("Bootstrap draft response was empty.");
        }

        var trimmed = rawText.Trim();
        var direct = TryDeserialize(trimmed);
        if (direct is not null)
        {
            return direct;
        }

        var codeFenceStart = trimmed.IndexOf('{');
        var codeFenceEnd = trimmed.LastIndexOf('}');
        if (codeFenceStart >= 0 && codeFenceEnd > codeFenceStart)
        {
            var jsonSlice = trimmed[codeFenceStart..(codeFenceEnd + 1)];
            var nested = TryDeserialize(jsonSlice);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException("Bootstrap draft response was not valid JSON.");
    }

    private static BootstrapDraftPayload? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BootstrapDraftPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record BootstrapDraftPayload(
        string IdentityMarkdown,
        string SoulMarkdown,
        string UserMarkdown,
        string? Summary);
}
