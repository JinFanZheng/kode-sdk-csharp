using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;

namespace KodaClaw.ChannelHub;

public sealed class AutomationNotificationService : IAutomationNotificationService
{
    private const string DiagnosticSource = "koda.automation";

    private readonly IChannelSendService _sendService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public AutomationNotificationService(
        IChannelSendService sendService,
        IDiagnosticsService? diagnosticsService = null)
    {
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _diagnosticsService = diagnosticsService;
    }

    public async Task<IReadOnlyList<ChannelPushResult>> PushAsync(
        IReadOnlyList<string> bindingIds,
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChannelPushResult>(bindingIds.Count);
        foreach (var bindingId in bindingIds)
        {
            try
            {
                var sendResult = await _sendService.SendAsync(bindingId, text, cancellationToken: cancellationToken);
                results.Add(new ChannelPushResult(bindingId, Ok: sendResult.Ok, ErrorMessage: null, SentAt: sendResult.SentAt));
            }
            catch (Exception ex)
            {
                _diagnosticsService?.Record(new DiagnosticEvent(
                    Id: $"diag-{Guid.NewGuid():N}",
                    Source: DiagnosticSource,
                    EventType: "automation.notification.push_failed",
                    Level: "warning",
                    Message: $"Failed to push notification to binding '{bindingId}': {ex.Message}",
                    Timestamp: DateTimeOffset.UtcNow,
                    Attributes: new Dictionary<string, string?>
                    {
                        ["bindingId"] = bindingId,
                        ["exceptionType"] = ex.GetType().Name,
                    }));
                results.Add(new ChannelPushResult(bindingId, Ok: false, ErrorMessage: ex.Message, SentAt: null));
            }
        }
        return results;
    }
}
