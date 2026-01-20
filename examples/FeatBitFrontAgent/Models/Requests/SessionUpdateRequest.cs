namespace Kode.Agent.WebApiAssistant.Models.Requests;

/// <summary>
/// 更新会话请求
/// </summary>
public class SessionUpdateRequest
{
    /// <summary>
    /// 新的会话标题
    /// </summary>
    public string? Title { get; set; }
}
