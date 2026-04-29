namespace KodaClaw.Runtime.Sessions;

public sealed record ChannelReplyProposal(
    string Action,
    string? ReplyText,
    string Reason,
    double Confidence)
{
    public bool ProposesReply => string.Equals(Action, "propose_reply", StringComparison.OrdinalIgnoreCase);
}
