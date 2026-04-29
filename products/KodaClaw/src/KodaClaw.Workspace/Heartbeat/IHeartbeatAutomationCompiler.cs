using KodaClaw.Contracts.Automations;

namespace KodaClaw.Workspace.Heartbeat;

public interface IHeartbeatAutomationCompiler
{
    IReadOnlyList<AutomationDefinition> Compile(string markdown);
}
