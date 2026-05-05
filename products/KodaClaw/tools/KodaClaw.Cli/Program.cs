using System.CommandLine;
using KodaClaw.Cli.Commands;

var root = new RootCommand("kc — KodaClaw management CLI");

root.AddCommand(AuthCommands.Build());
root.AddCommand(WorkspaceCommands.Build());
root.AddCommand(AutomationCommands.Build());
root.AddCommand(JobCommands.Build());
root.AddCommand(InboxCommands.Build());
root.AddCommand(SkillCommands.Build());

return await root.InvokeAsync(args);
