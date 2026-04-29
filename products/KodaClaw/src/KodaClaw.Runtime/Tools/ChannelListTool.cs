using KodaClaw.Contracts.Channels;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class ChannelListArgs
{
    [ToolParameter(Required = false, Description = "Optional filter by connector kind. Valid values: Telegram, GenericWebhook.")]
    public string? ConnectorKind { get; init; }
}

public sealed class ChannelListTool : ToolBase<ChannelListArgs>
{
    private readonly IThreadBindingRepository _bindingRepository;

    public ChannelListTool(IThreadBindingRepository bindingRepository)
    {
        ArgumentNullException.ThrowIfNull(bindingRepository);
        _bindingRepository = bindingRepository;
    }

    public override string Name => "channel_list";

    public override string Description =>
        "List all connected channel thread bindings. Returns bindingId, display title, connector kind, " +
        "thread type, and last inbound timestamp. Use this to discover bindingId values for channel_send.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ChannelListArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, RequiresApproval = false };

    protected override async Task<ToolResult> ExecuteAsync(
        ChannelListArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        ChannelConnectorKind? connectorKindFilter = null;
        if (!string.IsNullOrWhiteSpace(args.ConnectorKind))
        {
            if (!Enum.TryParse<ChannelConnectorKind>(args.ConnectorKind, ignoreCase: true, out var parsed))
            {
                return ToolResult.Fail(
                    $"Unknown connectorKind '{args.ConnectorKind}'. Valid values: {string.Join(", ", Enum.GetNames<ChannelConnectorKind>())}");
            }

            connectorKindFilter = parsed;
        }

        var query = new ChannelQuery(ConnectorKind: connectorKindFilter);
        var bindings = await _bindingRepository.ListAsync(query, cancellationToken);

        var items = bindings.Select(b => new
        {
            bindingId = b.Id,
            displayTitle = b.ChannelIdentity?.DisplayName ?? b.ChannelIdentity?.Username ?? b.ExternalThreadId,
            connectorKind = b.ConnectorKind.ToString(),
            threadType = b.ThreadType.ToString(),
            lastInboundAt = b.LastInboundAt?.ToString("O"),
        }).ToList();

        return ToolResult.Ok(items);
    }
}
