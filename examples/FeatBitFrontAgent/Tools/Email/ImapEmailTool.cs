using Kode.Agent.WebApiAssistant.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MimeKit;

namespace Kode.Agent.WebApiAssistant.Tools.Email;

/// <summary>
/// IMAP 邮件工具实现（使用 MailKit）
/// </summary>
public class ImapEmailTool : IEmailTool, IDisposable
{
    private readonly ILogger<ImapEmailTool> _logger;
    private readonly EmailOptions _options;
    private readonly ImapClient _imapClient;
    private readonly SmtpClient _smtpClient;

    public ImapEmailTool(ILogger<ImapEmailTool> logger, EmailOptions options)
    {
        _logger = logger;
        _options = options;
        _imapClient = new ImapClient();
        _smtpClient = new SmtpClient();
    }

    private async Task EnsureImapConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (!_imapClient.IsConnected)
        {
            await _imapClient.ConnectAsync(_options.Imap.Host, _options.Imap.Port, _options.Imap.UseSsl, cancellationToken);
            await _imapClient.AuthenticateAsync(_options.Imap.Username, _options.Imap.Password, cancellationToken);
            _logger.LogInformation("Connected to IMAP server: {Host}", _options.Imap.Host);
        }
    }

    private async Task EnsureSmtpConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (!_smtpClient.IsConnected)
        {
            await _smtpClient.ConnectAsync(_options.Smtp.Host, _options.Smtp.Port, _options.Smtp.UseSsl, cancellationToken);
            await _smtpClient.AuthenticateAsync(_options.Smtp.Username, _options.Smtp.Password, cancellationToken);
            _logger.LogInformation("Connected to SMTP server: {Host}", _options.Smtp.Host);
        }
    }

    public async Task<IReadOnlyList<EmailMessage>> ListEmailsAsync(
        string? folder = null,
        int limit = 20,
        bool unreadOnly = false,
        string? from = null,
        string? subject = null)
    {
        try
        {
            await EnsureImapConnectedAsync();

            var folderName = folder ?? "INBOX";
            _logger.LogInformation("Listing emails in {Folder}, limit: {Limit}", folderName, limit);

            var imapFolder = await _imapClient.GetFolderAsync(folderName);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly);

            var query = SearchQuery.All;
            if (unreadOnly)
            {
                query = SearchQuery.NotSeen;
            }

            var results = await imapFolder.SearchAsync(query);

            var emailList = new List<EmailMessage>();
            var count = 0;

            foreach (var uid in results)
            {
                if (count >= limit)
                    break;

                var message = await imapFolder.GetMessageAsync(uid, CancellationToken.None);

                // Filter by sender
                if (!string.IsNullOrEmpty(from) && message?.From?.Any() != true)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(from) && message?.From?.Any() == true)
                {
                    var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                    if (!fromAddress.Contains(from, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                // Filter by subject
                if (!string.IsNullOrEmpty(subject) && message?.Subject != null)
                {
                    if (!message.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                emailList.Add(new EmailMessage
                {
                    MessageId = uid.Id.ToString(),
                    From = message!.From?.Mailboxes.FirstOrDefault()?.Address ?? "",
                    To = message.To?.Mailboxes.Select(m => m?.Address ?? "").ToList() ?? new List<string>(),
                    Subject = message.Subject ?? "",
                    Body = "", // Only fetch body when reading
                    ReceivedDate = message.Date == default ? DateTime.Now : message.Date.DateTime,
                    IsRead = false, // Will fetch flags separately if needed
                    Folder = folderName
                });

                count++;
            }

            await imapFolder.CloseAsync();

            _logger.LogInformation("Found {Count} emails in {Folder}", emailList.Count, folderName);
            return emailList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list emails");
            return Array.Empty<EmailMessage>();
        }
    }

    public async Task<EmailMessage?> ReadEmailAsync(string messageId, string? folder = null)
    {
        try
        {
            await EnsureImapConnectedAsync();

            var folderName = folder ?? "INBOX";
            _logger.LogInformation("Reading email {MessageId} from {Folder}", messageId, folderName);

            var imapFolder = await _imapClient.GetFolderAsync(folderName);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly);

            if (!UniqueId.TryParse(messageId, out var uid))
            {
                await imapFolder.CloseAsync();
                return null;
            }

            var message = await imapFolder.GetMessageAsync(uid, CancellationToken.None);
            if (message == null)
            {
                await imapFolder.CloseAsync();
                return null;
            }

            var textBody = message.TextBody ?? string.Empty;

            await imapFolder.CloseAsync();

            return new EmailMessage
            {
                MessageId = messageId,
                From = message.From?.ToString() ?? "",
                To = message.To?.Mailboxes.Select(m => m.ToString() ?? "").ToList() ?? new List<string>(),
                Cc = message.Cc?.Mailboxes.Select(m => m.ToString() ?? "").ToList() ?? new List<string>(),
                Subject = message.Subject ?? "",
                Body = textBody,
                ReceivedDate = message.Date == default ? DateTime.Now : message.Date.DateTime,
                IsRead = true, // Mark as read when opened
                Folder = folderName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read email {MessageId}", messageId);
            return null;
        }
    }

    public async Task<string> SendEmailAsync(
        List<string> to,
        string subject,
        string body,
        List<string>? cc = null)
    {
        try
        {
            await EnsureSmtpConnectedAsync();

            _logger.LogInformation("Sending email to {To}, subject: {Subject}", string.Join(", ", to), subject);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));

            foreach (var recipient in to)
            {
                message.To.Add(new MailboxAddress("", recipient));
            }

            if (cc != null)
            {
                foreach (var recipient in cc)
                {
                    message.Cc.Add(new MailboxAddress("", recipient));
                }
            }

            message.Subject = subject;

            var builder = new BodyBuilder
            {
                TextBody = body
            };
            message.Body = builder.ToMessageBody();

            var response = await _smtpClient.SendAsync(message);
            _logger.LogInformation("Email sent successfully: {MessageId}", response);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email");
            throw;
        }
    }

    public async Task<string> SaveDraftAsync(
        List<string> to,
        string subject,
        string body)
    {
        try
        {
            await EnsureImapConnectedAsync();

            _logger.LogInformation("Saving draft: {Subject}", subject);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));

            foreach (var recipient in to)
            {
                message.To.Add(new MailboxAddress("", recipient));
            }

            message.Subject = subject;

            var builder = new BodyBuilder
            {
                TextBody = body
            };
            message.Body = builder.ToMessageBody();

            var draftsFolder = await _imapClient.GetFolderAsync("Drafts");
            await draftsFolder.OpenAsync(FolderAccess.ReadWrite);
            await draftsFolder.AppendAsync(message);
            await draftsFolder.CloseAsync();

            var messageId = message.MessageId ?? Guid.NewGuid().ToString("N");
            _logger.LogInformation("Draft saved: {MessageId}", messageId);
            return messageId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save draft");
            throw;
        }
    }

    public async Task<bool> MoveEmailAsync(string messageId, string toFolder)
    {
        try
        {
            await EnsureImapConnectedAsync();

            _logger.LogInformation("Moving email {MessageId} to {Folder}", messageId, toFolder);

            if (!UniqueId.TryParse(messageId, out var uid))
            {
                return false;
            }

            var sourceFolder = await _imapClient.GetFolderAsync("INBOX");
            await sourceFolder.OpenAsync(FolderAccess.ReadWrite);

            var targetFolder = await _imapClient.GetFolderAsync(toFolder);

            await sourceFolder.MoveToAsync(uid, targetFolder);
            await sourceFolder.CloseAsync();

            _logger.LogInformation("Email moved successfully: {MessageId} -> {Folder}", messageId, toFolder);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move email {MessageId}", messageId);
            return false;
        }
    }

    public async Task<bool> DeleteEmailAsync(string messageId)
    {
        try
        {
            await EnsureImapConnectedAsync();

            _logger.LogInformation("Deleting email {MessageId}", messageId);

            if (!UniqueId.TryParse(messageId, out var uid))
            {
                return false;
            }

            var folder = await _imapClient.GetFolderAsync("INBOX");
            await folder.OpenAsync(FolderAccess.ReadWrite);

            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true);
            await folder.ExpungeAsync();
            await folder.CloseAsync();

            _logger.LogInformation("Email deleted successfully: {MessageId}", messageId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete email {MessageId}", messageId);
            return false;
        }
    }

    public void Dispose()
    {
        _imapClient.Dispose();
        _smtpClient.Dispose();
    }
}
