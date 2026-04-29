using System.Text.Json;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Runtime.Prompt;

namespace KodaClaw.Runtime.Sessions;

public static class SessionPromptReportStore
{
    public const string FileName = "prompt-report.json";
    public const string HistoryFileName = "prompt-report-history.json";
    private const int MaxHistoryEntries = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        string sessionDirectory,
        PromptBuildResult prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(prompt);

        Directory.CreateDirectory(sessionDirectory);
        var path = GetPath(sessionDirectory);
        var report = new PromptReport(
            ProfileId: prompt.ProfileId.ToString(),
            SystemPrompt: prompt.SystemPrompt,
            CharacterCount: prompt.CharacterCount,
            LoadedContextFiles: prompt.LoadedContextFiles,
            GeneratedAt: DateTimeOffset.UtcNow,
            CharacterBudget: prompt.CharacterBudget,
            RemainingCharacterBudget: prompt.RemainingCharacterBudget,
            WasTruncated: prompt.WasTruncated,
            TruncatedContextFiles: prompt.TruncatedContextFiles,
            TruncationNotes: prompt.TruncationNotes);

        var existingHistory = await TryReadHistoryAsync(sessionDirectory, cancellationToken) ?? [];
        var nextHistory = BuildNextHistory(report, existingHistory);

        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        }

        await using var historyStream = File.Create(GetHistoryPath(sessionDirectory));
        await JsonSerializer.SerializeAsync(historyStream, nextHistory, JsonOptions, cancellationToken);
    }

    public static async Task<PromptReport?> TryReadAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        var path = GetPath(sessionDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PromptReport>(stream, JsonOptions, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<PromptReport>?> TryReadHistoryAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        var path = GetHistoryPath(sessionDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var history = await JsonSerializer.DeserializeAsync<List<PromptReport>>(stream, JsonOptions, cancellationToken);
            return history;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string GetPath(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        return Path.Combine(sessionDirectory, FileName);
    }

    public static string GetHistoryPath(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        return Path.Combine(sessionDirectory, HistoryFileName);
    }

    private static IReadOnlyList<PromptReport> BuildNextHistory(
        PromptReport current,
        IReadOnlyList<PromptReport> existingHistory)
    {
        if (existingHistory.Count > 0 && AreEquivalent(existingHistory[0], current))
        {
            var updated = existingHistory.ToList();
            updated[0] = current;
            return updated;
        }

        var next = new List<PromptReport>(capacity: Math.Min(existingHistory.Count + 1, MaxHistoryEntries))
        {
            current,
        };
        next.AddRange(existingHistory.Take(MaxHistoryEntries - 1));
        return next;
    }

    private static bool AreEquivalent(PromptReport left, PromptReport right)
    {
        return string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
               string.Equals(left.SystemPrompt, right.SystemPrompt, StringComparison.Ordinal) &&
               left.CharacterCount == right.CharacterCount &&
               left.CharacterBudget == right.CharacterBudget &&
               left.RemainingCharacterBudget == right.RemainingCharacterBudget &&
               left.WasTruncated == right.WasTruncated &&
               SequenceEqual(left.LoadedContextFiles, right.LoadedContextFiles) &&
               SequenceEqual(left.TruncatedContextFiles, right.TruncatedContextFiles) &&
               SequenceEqual(left.TruncationNotes, right.TruncationNotes);
    }

    private static bool SequenceEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
