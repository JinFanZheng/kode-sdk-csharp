using System.Globalization;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.ChannelHub;

public sealed class ChannelThreadSummaryWriter : IChannelThreadSummaryWriter
{
    private const string ChannelsSubPath = "workspace/channels";
    private const string SummaryFileName = "SUMMARY.md";
    private const int MaxSummaryLines = 60;

    private readonly IWorkspaceService _workspaceService;
    private readonly IModelProvider? _modelProvider;
    private readonly ChannelSessionOptions _options;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;

    public ChannelThreadSummaryWriter(
        IWorkspaceService workspaceService,
        IModelProvider? modelProvider = null,
        ChannelSessionOptions? options = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _modelProvider = modelProvider;
        _options = options ?? new ChannelSessionOptions();
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
    }

    public async Task WriteAsync(
        ThreadBinding binding,
        ChannelTurnOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channelDir = Path.Combine(
            _workspaceService.RootPath,
            ChannelsSubPath,
            SanitizePathSegment(binding.Id));
        Directory.CreateDirectory(channelDir);

        var filePath = Path.Combine(channelDir, SummaryFileName);
        var entry = FormatEntry(binding, outcome);

        await File.AppendAllTextAsync(filePath, entry, cancellationToken);
        await CompressOrTrimAsync(filePath, cancellationToken);
    }

    private async Task CompressOrTrimAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);

        if (lines.Length > _options.SummaryCompressionThreshold)
        {
            // KC-2206: Threshold exceeded — attempt LLM semantic compression if provider is available,
            // otherwise fall back to simple tail-truncation to SummaryCompressionTargetLines.
            if (_modelProvider is not null)
            {
                await TryCompressWithLlmAsync(filePath, lines, cancellationToken);
            }
            else
            {
                // No LLM provider — truncate to target lines directly.
                var targetLines = _options.SummaryCompressionTargetLines;
                var trimmed = lines.Length > targetLines ? lines[^targetLines..] : lines;
                await File.WriteAllLinesAsync(filePath, trimmed, cancellationToken);
            }
        }
        else if (lines.Length > MaxSummaryLines)
        {
            // Below compression threshold but above legacy window — simple tail-truncation.
            var trimmed = lines[^MaxSummaryLines..];
            await File.WriteAllLinesAsync(filePath, trimmed, cancellationToken);
        }
    }

    private async Task TryCompressWithLlmAsync(
        string filePath,
        string[] lines,
        CancellationToken cancellationToken)
    {
        var targetLines = _options.SummaryCompressionTargetLines;
        var compressCount = lines.Length - targetLines;

        if (compressCount <= 0)
        {
            return;
        }

        var oldLines = lines[..compressCount];
        var recentLines = lines[^targetLines..];
        var oldContent = string.Join(Environment.NewLine, oldLines);

        try
        {
            var model = ResolveModel();
            var response = await _modelProvider!.CompleteAsync(
                new ModelRequest
                {
                    Model = model,
                    Messages =
                    [
                        Message.User(
                            $"以下是一段 Koda channel 对话记录，请用 3-5 条 bullet points 总结关键信息和结论，保持简洁：\n{oldContent}")
                    ],
                    MaxTokens = 512,
                    Temperature = 0.2,
                },
                cancellationToken);

            var summary = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text)).Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                // Unexpected empty response — fall back to truncation.
                await File.WriteAllLinesAsync(filePath, recentLines, cancellationToken);
                return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
            var header = $"## Compressed History ({timestamp}){Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}---";

            var newLines = new List<string> { header, string.Empty };
            newLines.AddRange(recentLines);

            await File.WriteAllLinesAsync(filePath, newLines, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // LLM compression failed — fall back to simple truncation.
            await File.WriteAllLinesAsync(filePath, recentLines, cancellationToken);
        }
    }

    private string ResolveModel()
    {
        if (_runtimeConfigurationResolver is not null)
        {
            var snapshot = _runtimeConfigurationResolver.Resolve();
            if (!string.IsNullOrWhiteSpace(snapshot.DefaultModel))
            {
                return snapshot.DefaultModel.Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(_options.Model)
            ? _options.Model.Trim()
            : "koda-main";
    }

    private static string FormatEntry(ThreadBinding binding, ChannelTurnOutcome outcome)
    {
        var timestamp = outcome.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
        var kindLabel = outcome.Kind switch
        {
            ChannelTurnOutcomeKind.Delivered => "delivered",
            ChannelTurnOutcomeKind.DraftCreated => "draft_created",
            ChannelTurnOutcomeKind.ApprovalRequested => "approval_requested",
            _ => outcome.Kind.ToString().ToLowerInvariant(),
        };

        var preview = NormalizePreview(outcome.Summary);
        return $"- [{timestamp}] {kindLabel}: {preview}{Environment.NewLine}";
    }

    private static string NormalizePreview(string text)
    {
        const int maxLength = 120;
        var normalized = text.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }
}
