using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.WebApiAssistant.Tools.Email;

namespace Kode.Agent.WebApiAssistant.Tools.Agent;

/// <summary>
/// Tool for listing emails.
/// </summary>
[Tool("email_list")]
public sealed class EmailListTool : ToolBase<EmailListArgs>
{
    private readonly IEmailTool _emailTool;

    public EmailListTool(IEmailTool emailTool)
    {
        _emailTool = emailTool;
    }

    public override string Name => "email_list";

    public override string Description =>
        "List emails from the mailbox. " +
        "Supports filtering by folder, unread status, sender, and subject.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<EmailListArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    protected override async Task<ToolResult> ExecuteAsync(
        EmailListArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var emails = await _emailTool.ListEmailsAsync(
                args.Folder,
                args.Limit ?? 20,
                args.UnreadOnly ?? false,
                args.From,
                args.Subject);

            return ToolResult.Ok(new
            {
                emails = emails.Select(e => new
                {
                    messageId = e.MessageId,
                    from = e.From,
                    to = e.To,
                    subject = e.Subject,
                    receivedDate = e.ReceivedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                    isRead = e.IsRead,
                    folder = e.Folder
                }),
                count = emails.Count
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to list emails: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for email_list tool.
/// </summary>
[GenerateToolSchema]
public class EmailListArgs
{
    [ToolParameter(Description = "Folder name (e.g., INBOX, Sent)", Required = false)]
    public string? Folder { get; init; }

    [ToolParameter(Description = "Maximum number of emails to return", Required = false)]
    public int? Limit { get; init; }

    [ToolParameter(Description = "Only show unread emails", Required = false)]
    public bool? UnreadOnly { get; init; }

    [ToolParameter(Description = "Filter by sender email", Required = false)]
    public string? From { get; init; }

    [ToolParameter(Description = "Filter by subject keyword", Required = false)]
    public string? Subject { get; init; }
}

/// <summary>
/// Tool for reading email content.
/// </summary>
[Tool("email_read")]
public sealed class EmailReadTool : ToolBase<EmailReadArgs>
{
    private readonly IEmailTool _emailTool;

    public EmailReadTool(IEmailTool emailTool)
    {
        _emailTool = emailTool;
    }

    public override string Name => "email_read";

    public override string Description =>
        "Read the full content of an email including body.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<EmailReadArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    protected override async Task<ToolResult> ExecuteAsync(
        EmailReadArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var email = await _emailTool.ReadEmailAsync(args.MessageId, args.Folder);

            if (email == null)
            {
                return ToolResult.Fail($"Email not found: {args.MessageId}");
            }

            return ToolResult.Ok(new
            {
                messageId = email.MessageId,
                from = email.From,
                to = email.To,
                cc = email.Cc,
                subject = email.Subject,
                body = email.Body,
                receivedDate = email.ReceivedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                isRead = email.IsRead,
                folder = email.Folder
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to read email: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for email_read tool.
/// </summary>
[GenerateToolSchema]
public class EmailReadArgs
{
    [ToolParameter(Description = "Message ID to read")]
    public required string MessageId { get; init; }

    [ToolParameter(Description = "Folder name (optional)", Required = false)]
    public string? Folder { get; init; }
}

/// <summary>
/// Tool for saving email drafts.
/// </summary>
[Tool("email_draft")]
public sealed class EmailDraftTool : ToolBase<EmailDraftArgs>
{
    private readonly IEmailTool _emailTool;

    public EmailDraftTool(IEmailTool emailTool)
    {
        _emailTool = emailTool;
    }

    public override string Name => "email_draft";

    public override string Description =>
        "Save an email as a draft. " +
        "Returns the draft message ID.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<EmailDraftArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false
    };

    protected override async Task<ToolResult> ExecuteAsync(
        EmailDraftArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var draftId = await _emailTool.SaveDraftAsync(
                args.To,
                args.Subject,
                args.Body);

            return ToolResult.Ok(new
            {
                draftId,
                message = "Email draft saved successfully"
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to save draft: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for email_draft tool.
/// </summary>
[GenerateToolSchema]
public class EmailDraftArgs
{
    [ToolParameter(Description = "Recipient email addresses")]
    public required List<string> To { get; init; }

    [ToolParameter(Description = "Email subject")]
    public required string Subject { get; init; }

    [ToolParameter(Description = "Email body content")]
    public required string Body { get; init; }
}

/// <summary>
/// Tool for moving emails between folders.
/// </summary>
[Tool("email_move")]
public sealed class EmailMoveTool : ToolBase<EmailMoveArgs>
{
    private readonly IEmailTool _emailTool;

    public EmailMoveTool(IEmailTool emailTool)
    {
        _emailTool = emailTool;
    }

    public override string Name => "email_move";

    public override string Description =>
        "Move an email to a different folder.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<EmailMoveArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false
    };

    protected override async Task<ToolResult> ExecuteAsync(
        EmailMoveArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await _emailTool.MoveEmailAsync(args.MessageId, args.ToFolder);

            if (success)
            {
                return ToolResult.Ok(new
                {
                    moved = true,
                    messageId = args.MessageId,
                    toFolder = args.ToFolder,
                    message = "Email moved successfully"
                });
            }
            else
            {
                return ToolResult.Fail($"Failed to move email: {args.MessageId}");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to move email: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for email_move tool.
/// </summary>
[GenerateToolSchema]
public class EmailMoveArgs
{
    [ToolParameter(Description = "Message ID to move")]
    public required string MessageId { get; init; }

    [ToolParameter(Description = "Target folder name")]
    public required string ToFolder { get; init; }
}
