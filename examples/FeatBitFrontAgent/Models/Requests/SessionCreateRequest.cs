namespace Kode.Agent.WebApiAssistant.Models.Requests;

/// <summary>
/// 创建会话请求
/// </summary>
public class SessionCreateRequest
{
    /// <summary>
    /// 会话标题（可选）
    /// </summary>
    public string? Title { get; set; }
}
