namespace KodaClaw.ChannelHub.Send;

public interface IChannelSendCapture
{
    void Record(string bindingId, string text);
    IReadOnlyList<string> GetAndClear(string bindingId);
}
