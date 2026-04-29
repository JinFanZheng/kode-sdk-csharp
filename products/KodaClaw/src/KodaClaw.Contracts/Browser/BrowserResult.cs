namespace KodaClaw.Contracts.Browser;

/// <summary>
/// 浏览器操作的统一返回类型。
/// 泛型参数 <typeparamref name="T"/> 是成功时的数据载荷；
/// 当 <see cref="Ok"/> 为 <c>false</c> 时，<see cref="Data"/> 为 <c>null</c>，
/// 错误详情见 <see cref="Error"/> 和 <see cref="ErrorCode"/>。
/// </summary>
public sealed record BrowserResult<T>(
    bool Ok,
    T? Data,
    string? Error = null,
    string? ErrorCode = null);
