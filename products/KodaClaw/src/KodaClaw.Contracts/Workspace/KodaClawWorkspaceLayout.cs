namespace KodaClaw.Contracts.Workspace;

public static class KodaClawWorkspaceLayout
{
    public const string RootDirectoryName = ".kodaclaw";
    public const int CurrentWorkspaceVersion = 1;

    public const string ConfigDirectory = "config";
    public const string IdentityDirectory = "identity";
    public const string WorkspaceDirectory = "workspace";
    public const string SessionsDirectory = "sessions";
    public const string LogsDirectory = "logs";
    public const string CacheDirectory = "cache";

    public const string AppConfigFile = "app.json";
    public const string GatewayConfigFile = "gateway.json";
    public const string ModelsConfigFile = "models.json";
    public const string PluginsConfigFile = "plugins.json";
    public const string ControlPlaneDatabaseFile = "control-plane.db";
    public const string SecretMigrationReportFile = "secret-migration-report.json";
    public const string ImportRepairReportFile = "import-repair-report.json";
    public const string StartupRepairReportFile = "startup-repair-report.json";
    public const string UpdateStateFile = "update-state.json";
    public const string BackupManifestFile = "backup-manifest.json";

    public const string DeviceIdentityFile = "device.json";
    public const string ProfileFile = "profile.json";

    public const string AgentsFile = "AGENTS.md";
    public const string IdentityFile = "IDENTITY.md";
    public const string SoulFile = "SOUL.md";
    public const string OntologyFile = "ONTOLOGY.md";
    public const string UserFile = "USER.md";
    public const string MemoryFile = "MEMORY.md";
    public const string HeartbeatFile = "HEARTBEAT.md";
    public const string BootstrapFile = "BOOTSTRAP.md";
    public const string ToolsFile = "TOOLS.md";
    public const string McpConfigFile = "mcp.json";

    public const string DiagnosticsJournalFile = "logs/diagnostics.jsonl";

    public const string WorkspaceSkillsDirectory = "workspace/skills";
    public const string MediaDirectory = "media";

    public const string MemorySessionsDirectory = "workspace/memory/sessions";
    public const string MemorySessionsPendingDirectory = "workspace/memory/sessions/.pending";
    public const string MemoryTopicsDirectory = "workspace/memory/topics";
    public const string MemoryDormantDirectory = "workspace/memory/dormant";
    public const string MemoryArchiveDirectory = "workspace/memory/archive";
}
