namespace KodaClaw.Contracts.System;

public sealed record SystemStatsEvent(
    int ErrorCount,
    int WarningCount,
    int InboxUnreadCount);
