namespace KodaClaw.Contracts.Inbox;

public sealed record InboxQueryResponse(
    IReadOnlyList<InboxItem> Items);
