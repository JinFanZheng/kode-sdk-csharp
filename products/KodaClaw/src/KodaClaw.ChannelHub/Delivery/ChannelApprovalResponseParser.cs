namespace KodaClaw.ChannelHub.Delivery;

/// <summary>
/// Parses a plain-text message to detect whether it is an approval response.
/// Recognises: approve keywords (ok/yes/approve/send) and reject keywords (no/cancel/reject),
/// optionally followed by a 6-character uppercase hex token (e.g. "ok A3F9C1").
/// </summary>
public static class ChannelApprovalResponseParser
{
    private static readonly string[] ApproveKeywords = ["ok", "yes", "approve", "send"];
    private static readonly string[] RejectKeywords = ["no", "cancel", "reject"];

    /// <summary>
    /// Returns an <see cref="ApprovalResponseIntent"/> if the text looks like an approval
    /// response, otherwise returns <c>null</c>. Falls through without matching if:
    /// <list type="bullet">
    ///   <item>The text is empty or whitespace.</item>
    ///   <item>It contains more than two space-separated words.</item>
    ///   <item>A second word is present but is not a valid 6-hex-char token.</item>
    /// </list>
    /// </summary>
    public static ApprovalResponseIntent? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2)
        {
            return null;
        }

        var keyword = parts[0].ToLowerInvariant();
        string? token = null;

        if (parts.Length == 2)
        {
            var candidate = parts[1].ToUpperInvariant();
            if (!IsValidToken(candidate))
            {
                return null;
            }

            token = candidate;
        }

        if (ApproveKeywords.Contains(keyword))
        {
            return new ApprovalResponseIntent(ApprovalAction.Approve, token);
        }

        if (RejectKeywords.Contains(keyword))
        {
            return new ApprovalResponseIntent(ApprovalAction.Reject, token);
        }

        return null;
    }

    private static bool IsValidToken(string candidate)
    {
        if (candidate.Length != 6)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record ApprovalResponseIntent(ApprovalAction Action, string? Token);

public enum ApprovalAction
{
    Approve = 0,
    Reject = 1,
}
