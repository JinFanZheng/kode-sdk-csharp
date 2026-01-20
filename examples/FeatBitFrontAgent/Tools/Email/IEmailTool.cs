namespace Kode.Agent.WebApiAssistant.Tools.Email;

/// <summary>
/// 邮件消息
/// </summary>
public class EmailMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string? From { get; set; }
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime ReceivedDate { get; set; }
    public bool IsRead { get; set; }
    public string Folder { get; set; } = "INBOX";
}

/// <summary>
/// 邮件工具接口
/// </summary>
public interface IEmailTool
{
    /// <summary>
    /// 列出邮件
    /// </summary>
    Task<IReadOnlyList<EmailMessage>> ListEmailsAsync(
        string? folder = null,
        int limit = 20,
        bool unreadOnly = false,
        string? from = null,
        string? subject = null);

    /// <summary>
    /// 读取邮件内容
    /// </summary>
    Task<EmailMessage?> ReadEmailAsync(string messageId, string? folder = null);

    /// <summary>
    /// 发送邮件
    /// </summary>
    Task<string> SendEmailAsync(
        List<string> to,
        string subject,
        string body,
        List<string>? cc = null);

    /// <summary>
    /// 保存草稿
    /// </summary>
    Task<string> SaveDraftAsync(
        List<string> to,
        string subject,
        string body);

    /// <summary>
    /// 移动邮件
    /// </summary>
    Task<bool> MoveEmailAsync(string messageId, string toFolder);

    /// <summary>
    /// 删除邮件
    /// </summary>
    Task<bool> DeleteEmailAsync(string messageId);
}
