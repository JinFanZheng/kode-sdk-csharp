namespace KodaClaw.Contracts.Chat;

public interface IChatSessionService
{
    IAsyncEnumerable<ChatStreamEvent> StreamMainSessionAsync(
        ChatStreamRequest request,
        CancellationToken cancellationToken = default);
}
