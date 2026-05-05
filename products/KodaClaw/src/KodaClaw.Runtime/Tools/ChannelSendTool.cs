using KodaClaw.Contracts.Channels;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class ChannelSendArgs
{
    [ToolParameter(Description = "The binding ID of the target channel thread. Found in session context (BindingId field) or via channel_list.")]
    public required string BindingId { get; init; }

    [ToolParameter(Description = "The message text to send to the channel thread.")]
    public required string Text { get; init; }

    [ToolParameter(Description = "Optional media ID of an image to attach. The image is sent alongside the text caption.", Required = false)]
    public string? MediaId { get; init; }

    [ToolParameter(Description = "Optional JSON metadata to pass to the connector layer (e.g. DingTalk ActionCard fields). Other connectors ignore this.", Required = false)]
    public string? Metadata { get; init; }

    [ToolParameter(Description = "Message format preference. Options: auto (default, connector decides), plain_text (force plain text), markdown (render Markdown formatting).", Required = false)]
    public string? Format { get; init; }

    [ToolParameter(Description = "Optional external message ID to reply to. Usually set by the channel runtime; omit for normal sends.", Required = false)]
    public string? ReplyToExternalMessageId { get; init; }
}

public sealed class ChannelSendTool : ToolBase<ChannelSendArgs>
{
    private readonly IChannelSendService _sendService;

    public ChannelSendTool(IChannelSendService sendService)
    {
        ArgumentNullException.ThrowIfNull(sendService);
        _sendService = sendService;
    }

    public override string Name =>
        "channel_send";

    public override string Description =>
        "Send a message to an external channel thread (e.g. Telegram DM or group). " +
        "Use the BindingId from the current channel session context, or discover bindings with channel_list. " +
        "Delivery is always immediate (AutoSend). In channel sessions, call this tool to reply to the inbound message. " +
        "In automation or main sessions, call this to proactively push updates to a connected channel.";

    public override object InputSchema =>
        JsonSchemaBuilder.BuildSchema<ChannelSendArgs>();

    public override ToolAttributes Attributes =>
        new() { ReadOnly = false, RequiresApproval = false };

    protected override async Task<ToolResult> ExecuteAsync(
        ChannelSendArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var format = args.Format?.ToLowerInvariant() switch
        {
            "plain_text" or "plaintext" => OutboundMessageFormat.PlainText,
            "markdown" => OutboundMessageFormat.Markdown,
            _ => OutboundMessageFormat.Auto,
        };
        var result = string.IsNullOrWhiteSpace(args.ReplyToExternalMessageId)
            ? await _sendService.SendAsync(args.BindingId, args.Text, args.MediaId, args.Metadata, format, cancellationToken)
            : await _sendService.SendReplyAsync(
                args.BindingId,
                args.Text,
                args.ReplyToExternalMessageId,
                args.MediaId,
                args.Metadata,
                format,
                cancellationToken);
        return ToolResult.Ok(new
        {
            ok = result.Ok,
            bindingId = result.BindingId,
            sentAt = result.SentAt.ToString("O"),
            externalMessageId = result.ExternalMessageId,
        });
    }
}
