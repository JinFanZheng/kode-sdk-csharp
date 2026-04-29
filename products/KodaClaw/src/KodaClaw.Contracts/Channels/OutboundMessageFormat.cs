namespace KodaClaw.Contracts.Channels;

/// <summary>
/// 消息格式偏好，指导 connector 如何渲染 MessageText。
/// </summary>
public enum OutboundMessageFormat
{
    /// <summary>connector 自行决定（保持现有行为不变）</summary>
    Auto = 0,
    /// <summary>明确要求纯文本</summary>
    PlainText = 1,
    /// <summary>要求尽量保留 Markdown 格式</summary>
    Markdown = 2,
}
