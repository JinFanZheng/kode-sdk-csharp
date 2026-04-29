using System.Text;

namespace KodaClaw.Runtime.Prompt;

public sealed record PromptContextDocument(string Path, string Content);

public sealed record PromptBuildResult(
    PromptProfileId ProfileId,
    string SystemPrompt,
    int CharacterCount,
    IReadOnlyList<string> LoadedContextFiles,
    int? CharacterBudget = null,
    int? RemainingCharacterBudget = null,
    bool WasTruncated = false,
    IReadOnlyList<string>? TruncatedContextFiles = null,
    IReadOnlyList<string>? TruncationNotes = null);

public sealed class PromptBuilder
{
    private readonly PromptProfile _profile;
    private readonly List<PromptSection> _sections = [];
    private readonly List<string> _loadedContextFiles = [];
    private readonly List<string> _truncatedContextFiles = [];
    private readonly List<string> _truncationNotes = [];
    private int? _characterBudget;

    public PromptBuilder(PromptProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        AddBody(profile.BaseInstruction);
        AddSection(
            "Prompt Profile",
            [
                $"Id: {profile.Id}",
                $"Title: {profile.Title}",
            ]);

        if (!string.IsNullOrWhiteSpace(profile.OverlayInstruction))
        {
            AddSection("Profile Overlay", profile.OverlayInstruction);
        }
    }

    public PromptBuilder WithCharacterBudget(int? characterBudget)
    {
        _characterBudget = characterBudget is > 0 ? characterBudget : null;
        return this;
    }

    public PromptBuilder AddSection(string title, IEnumerable<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(lines);

        var body = string.Join(
            Environment.NewLine,
            lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.TrimEnd()));
        if (string.IsNullOrWhiteSpace(body))
        {
            return this;
        }

        _sections.Add(new PromptSection(title.Trim(), body));
        return this;
    }

    public PromptBuilder AddSection(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (string.IsNullOrWhiteSpace(body))
        {
            return this;
        }

        _sections.Add(new PromptSection(title.Trim(), body.Trim()));
        return this;
    }

    public PromptBuilder AddBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return this;
        }

        _sections.Add(new PromptSection(null, body.Trim()));
        return this;
    }

    public PromptBuilder AddContextDocuments(
        IReadOnlyList<PromptContextDocument> documents,
        string emptyText = "(none)")
    {
        ArgumentNullException.ThrowIfNull(documents);

        var body = BuildContextDocumentsBody(documents, emptyText);
        _sections.Add(new PromptSection("Loaded Context Files", body));
        return this;
    }

    public PromptBuildResult Build()
    {
        var prompt = RenderSections(_sections).Trim();
        var remainingCharacterBudget = _characterBudget.HasValue
            ? (int?)Math.Max(_characterBudget.Value - prompt.Length, 0)
            : null;

        return new PromptBuildResult(
            _profile.Id,
            prompt,
            prompt.Length,
            _loadedContextFiles.AsReadOnly(),
            _characterBudget,
            remainingCharacterBudget,
            _truncatedContextFiles.Count > 0,
            _truncatedContextFiles.AsReadOnly(),
            _truncationNotes.AsReadOnly());
    }

    private string BuildContextDocumentsBody(
        IReadOnlyList<PromptContextDocument> documents,
        string emptyText)
    {
        var builder = new StringBuilder();
        if (documents.Count == 0)
        {
            builder.Append(emptyText.Trim());
            return builder.ToString().TrimEnd();
        }

        var remainingContextCharacters = ResolveRemainingContextCharacters();
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            _loadedContextFiles.Add(document.Path);

            var header = $"### File: {document.Path}";
            var separator = index + 1 < documents.Count ? $"{Environment.NewLine}{Environment.NewLine}" : string.Empty;
            var normalizedContent = document.Content.TrimEnd();
            var fullBlock = $"{header}{Environment.NewLine}{normalizedContent}{separator}";

            if (remainingContextCharacters is null)
            {
                builder.Append(fullBlock);
                continue;
            }

            if (remainingContextCharacters.Value <= 0)
            {
                _truncatedContextFiles.Add(document.Path);
                _truncationNotes.Add($"Omitted {document.Path} because the prompt character budget was exhausted.");
                continue;
            }

            if (fullBlock.Length <= remainingContextCharacters.Value)
            {
                builder.Append(fullBlock);
                remainingContextCharacters -= fullBlock.Length;
                continue;
            }

            var truncationSuffix = $"{Environment.NewLine}...[truncated]";
            var maxContentCharacters = remainingContextCharacters.Value - header.Length - Environment.NewLine.Length - separator.Length - truncationSuffix.Length;
            if (maxContentCharacters > 0)
            {
                var clippedContent = normalizedContent[..Math.Min(normalizedContent.Length, maxContentCharacters)].TrimEnd();
                builder.Append(header);
                builder.AppendLine();
                builder.Append(clippedContent);
                builder.Append(truncationSuffix);
                builder.Append(separator);
            }

            _truncatedContextFiles.Add(document.Path);
            _truncationNotes.Add($"Truncated {document.Path} to stay within the prompt character budget.");
            remainingContextCharacters = 0;
        }

        if (builder.Length == 0)
        {
            builder.Append("(context omitted: budget exhausted)");
        }

        return builder.ToString().TrimEnd();
    }

    private int? ResolveRemainingContextCharacters()
    {
        if (!_characterBudget.HasValue)
        {
            return null;
        }

        var currentPromptLength = RenderSections(_sections).Trim().Length;
        var sectionScaffoldLength = new PromptSection("Loaded Context Files", string.Empty).RenderedLength;
        return Math.Max(_characterBudget.Value - currentPromptLength - sectionScaffoldLength, 0);
    }

    private static string RenderSections(IReadOnlyList<PromptSection> sections)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                builder.AppendLine(section.Title);
            }

            builder.Append(section.Body);
            if (index + 1 < sections.Count)
            {
                builder.AppendLine();
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private sealed record PromptSection(string? Title, string Body)
    {
        public int RenderedLength =>
            (string.IsNullOrWhiteSpace(Title) ? 0 : Title!.Length + Environment.NewLine.Length) +
            Body.Length;
    }
}
