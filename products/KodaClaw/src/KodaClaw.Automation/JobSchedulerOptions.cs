namespace KodaClaw.Automation;

public sealed class JobSchedulerOptions
{
    public int MaxConcurrent { get; set; } = 5;

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);
}
