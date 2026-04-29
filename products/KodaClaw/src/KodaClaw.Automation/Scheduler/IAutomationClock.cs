namespace KodaClaw.Automation.Scheduler;

public interface IAutomationClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemAutomationClock : IAutomationClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeAutomationClock : IAutomationClock
{
    private DateTimeOffset _utcNow;

    public FakeAutomationClock(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public DateTimeOffset UtcNow => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
    }
}
