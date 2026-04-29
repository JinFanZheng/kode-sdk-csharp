using FluentAssertions;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using KodaClaw.ControlPlane;
using KodaClaw.PluginHost;
using KodaClaw.PluginHost.Hosting;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace KodaClaw.IntegrationTests.PluginHost;

internal sealed class PluginHostFixture : IAsyncDisposable
{
    private const string PluginId = "plugin.fixture";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ServiceProvider _provider;
    private readonly bool _failHealthPing;
    private readonly IReadOnlyDictionary<string, string>? _runtimeEnvironmentReferences;

    private PluginHostFixture(
        string rootPath,
        ServiceProvider provider,
        bool failHealthPing,
        IReadOnlyDictionary<string, string>? runtimeEnvironmentReferences)
    {
        RootPath = rootPath;
        _provider = provider;
        _failHealthPing = failHealthPing;
        _runtimeEnvironmentReferences = runtimeEnvironmentReferences;
    }

    public string RootPath { get; }

    public string PluginIdValue => PluginId;

    public IPluginLifecycleHost Host => _provider.GetRequiredService<IPluginLifecycleHost>();

    public IPluginRegistryRepository Registry => _provider.GetRequiredService<IPluginRegistryRepository>();

    public IPluginLogRepository Logs => _provider.GetRequiredService<IPluginLogRepository>();

    public IDiagnosticsService Diagnostics => _provider.GetRequiredService<IDiagnosticsService>();

    public static async Task<PluginHostFixture> CreateAsync(
        bool failHealthPing = false,
        Action<PluginHostOptions>? configureHost = null,
        IReadOnlyDictionary<string, string>? runtimeEnvironmentReferences = null,
        IReadOnlyDictionary<SecretRef, string>? seededSecrets = null)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "kodaclaw-plugin-host-integration",
            Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = rootPath);
        services.AddKodaClawJsonStore(rootPath);
        services.AddKodaClawControlPlane();
        services.AddKodaClawPluginHost(options =>
        {
            options.MaxRestartAttempts = 1;
            options.DefaultHealthcheckTimeoutSeconds = 5;
            configureHost?.Invoke(options);
        });

        var provider = services.BuildServiceProvider();
        if (seededSecrets is { Count: > 0 })
        {
            var secretStore = provider.GetRequiredService<ISecretStore>();
            foreach (var (secretRef, secretValue) in seededSecrets)
            {
                await secretStore.UpsertAsync(secretRef, secretValue);
            }
        }

        var fixture = new PluginHostFixture(
            rootPath,
            provider,
            failHealthPing,
            runtimeEnvironmentReferences);
        await fixture.InitializeAsync();
        return fixture;
    }

    public async Task<PluginRecord> ReloadAsync()
    {
        var record = await Registry.GetByIdAsync(PluginId);
        record.Should().NotBeNull();
        return record!;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        DeleteDirectoryWithRetry(RootPath);
    }

    private async Task InitializeAsync()
    {
        var workspaceService = _provider.GetRequiredService<IWorkspaceService>();
        await workspaceService.EnsureInitializedAsync();

        var pluginRoot = Path.Combine(
            RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "plugins",
            PluginId);
        Directory.CreateDirectory(pluginRoot);

        var record = BuildRecord(pluginRoot);
        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, "plugin.json"),
            JsonSerializer.Serialize(record.Manifest, JsonOptions));
        await Registry.UpsertAsync(record);
    }

    private PluginRecord BuildRecord(string pluginRoot)
    {
        var now = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
        var environment = _failHealthPing
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KODACLAW_FIXTURE_HEALTH_PING_FAIL"] = "1",
            }
            : null;

        var manifest = new PluginManifest(
            Id: PluginId,
            Name: "Fixture Plugin",
            Version: "0.1.0",
            Types: [PluginType.Tool],
            Runtime: new PluginRuntimeSpec(
                Transport: PluginTransportKind.Stdio,
                Command: ResolveFixtureCommand(),
                Args: ResolveFixtureArguments(),
                Environment: environment,
                EnvironmentReferences: _runtimeEnvironmentReferences),
            Permissions: new PluginPermissionSet(Background: true),
            Capabilities: new PluginCapabilitySet(Tools: ["echo"]),
            Healthcheck: new PluginHealthcheckSpec(
                ToolName: "health_ping",
                IntervalSeconds: 30,
                TimeoutSeconds: 5));

        return new PluginRecord(
            Id: PluginId,
            Manifest: manifest,
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRoot,
            TrustState: PluginTrustState.Trusted,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Stopped,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now);
    }

    private static string ResolveFixtureCommand()
    {
        var outputDirectory = ResolveFixtureOutputDirectory();
        var executableName = OperatingSystem.IsWindows()
            ? "KodaClaw.PluginFixtureServer.exe"
            : "KodaClaw.PluginFixtureServer";
        var executablePath = Path.Combine(outputDirectory, executableName);
        if (File.Exists(executablePath))
        {
            return executablePath;
        }

        return "dotnet";
    }

    private static IReadOnlyList<string> ResolveFixtureArguments()
    {
        var outputDirectory = ResolveFixtureOutputDirectory();
        var executableName = OperatingSystem.IsWindows()
            ? "KodaClaw.PluginFixtureServer.exe"
            : "KodaClaw.PluginFixtureServer";
        var executablePath = Path.Combine(outputDirectory, executableName);
        if (File.Exists(executablePath))
        {
            return [];
        }

        return [Path.Combine(outputDirectory, "KodaClaw.PluginFixtureServer.dll")];
    }

    private static string ResolveFixtureOutputDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var frameworkDirectory = new DirectoryInfo(baseDirectory);
        var configurationDirectory = frameworkDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test configuration directory.");
        var binDirectory = configurationDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test bin directory.");
        var integrationTestsDirectory = binDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test project directory.");

        var outputDirectory = Path.GetFullPath(Path.Combine(
            integrationTestsDirectory.FullName,
            "..",
            "Fixtures",
            "KodaClaw.PluginFixtureServer",
            "bin",
            configurationDirectory.Name,
            frameworkDirectory.Name));

        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Fixture server output directory was not found: '{outputDirectory}'.");
        }

        return outputDirectory;
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
