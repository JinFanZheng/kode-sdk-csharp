namespace KodaClaw.Contracts.System;

public sealed record ErrorResponse(string Code, string Message, string? RequestId = null);
