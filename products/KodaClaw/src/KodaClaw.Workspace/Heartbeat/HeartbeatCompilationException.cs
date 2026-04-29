namespace KodaClaw.Workspace.Heartbeat;

public sealed class HeartbeatCompilationException : Exception
{
    public HeartbeatCompilationException(string message, int? lineNumber = null)
        : base(lineNumber is null ? message : $"Line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public int? LineNumber { get; }
}
