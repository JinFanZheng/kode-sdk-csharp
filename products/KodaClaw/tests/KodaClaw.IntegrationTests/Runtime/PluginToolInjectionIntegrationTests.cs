using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Workspace;
using KodaClaw.ControlPlane;
using KodaClaw.PluginHost;
using KodaClaw.PluginHost.Hosting;
using KodaClaw.PluginHost.Trust;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class PluginToolInjectionIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task New_main_session_should_include_running_plugin_tools()
    {
        await using var fixture = await PluginRuntimeFixture.CreateAsync();
        await fixture.Host.StartAsync(fixture.PluginId);

        await using var service = fixture.CreateMainSessionService();
        var handle = await service.EnsureMainSessionAsync();
        var toolIds = await ReadToolIdsAsync(handle);

        toolIds.Should().Contain("mcp__plugin.fixture__echo");
        toolIds.Should().NotContain("mcp__plugin.fixture__health_ping");
    }

    [Fact]
    public async Task Stopped_plugin_should_not_be_injected_into_new_main_session()
    {
        await using var fixture = await PluginRuntimeFixture.CreateAsync();
        await fixture.Host.StartAsync(fixture.PluginId);

        await using (var firstService = fixture.CreateMainSessionService())
        {
            var firstHandle = await firstService.EnsureMainSessionAsync();
            var firstTools = await ReadToolIdsAsync(firstHandle);
            firstTools.Should().Contain("mcp__plugin.fixture__echo");
        }

        await fixture.Host.StopAsync(fixture.PluginId);
        await fixture.ClearActiveMainSessionAsync();

        await using var secondService = fixture.CreateMainSessionService();
        var secondHandle = await secondService.EnsureMainSessionAsync();
        var secondTools = await ReadToolIdsAsync(secondHandle);

        secondTools.Should().NotContain("mcp__plugin.fixture__echo");
    }

    [Fact]
    public async Task Degraded_plugin_should_not_be_injected_into_new_main_session()
    {
        await using var fixture = await PluginRuntimeFixture.CreateAsync(
            failHealthPing: true,
            maxRestartAttempts: 0);
        await fixture.Host.StartAsync(fixture.PluginId);

        var degraded = await fixture.Host.CheckHealthAsync(fixture.PluginId);
        degraded.RuntimeState.Should().Be(PluginRuntimeState.Degraded);

        await using var service = fixture.CreateMainSessionService();
        var handle = await service.EnsureMainSessionAsync();
        var toolIds = await ReadToolIdsAsync(handle);

        toolIds.Should().NotContain("mcp__plugin.fixture__echo");
    }

    [Fact]
    public async Task Signed_plugin_should_be_injected_into_new_main_session()
    {
        await using var fixture = await PluginRuntimeFixture.CreateAsync();
        await PromotePluginToSignedAsync(fixture);
        await fixture.Host.StartAsync(fixture.PluginId);

        await using var service = fixture.CreateMainSessionService();
        var handle = await service.EnsureMainSessionAsync();
        var toolIds = await ReadToolIdsAsync(handle);

        toolIds.Should().Contain("mcp__plugin.fixture__echo");
    }

    private static async Task<IReadOnlyList<string>> ReadToolIdsAsync(MainSessionHandle handle)
    {
        var metaPath = Path.Combine(handle.SessionDirectory, "meta.json");
        File.Exists(metaPath).Should().BeTrue();

        await using var stream = File.OpenRead(metaPath);
        using var document = await JsonDocument.ParseAsync(stream);

        if (!document.RootElement.TryGetProperty("metadata", out var metadataElement) ||
            metadataElement.ValueKind != JsonValueKind.Object ||
            !metadataElement.TryGetProperty("toolIds", out var toolIdsElement) ||
            toolIdsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return toolIdsElement
            .EnumerateArray()
            .Where(node => node.ValueKind == JsonValueKind.String)
            .Select(node => node.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static async Task PromotePluginToSignedAsync(PluginRuntimeFixture fixture)
    {
        var pluginRoot = Path.Combine(
            fixture.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "plugins",
            fixture.PluginId);

        await WritePluginSignatureAsync(pluginRoot, signer: "Fixture Publisher");

        var evaluator = new PluginTrustEvaluator();
        var evidence = await evaluator.EvaluateAsync(pluginRoot);
        var record = await fixture.Registry.GetByIdAsync(fixture.PluginId);
        record.Should().NotBeNull();

        await fixture.Registry.UpsertAsync(record! with
        {
            TrustState = PluginTrustState.Signed,
            TrustEvidence = evidence,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task WritePluginSignatureAsync(string pluginRoot, string signer)
    {
        var evaluator = new PluginTrustEvaluator();
        var digestOnly = await evaluator.EvaluateAsync(pluginRoot);
        var payload = JsonSerializer.Serialize(new
        {
            manifestDigestSha256 = digestOnly.ManifestDigestSha256,
            packageDigestSha256 = digestOnly.PackageDigestSha256,
            signer,
        });

        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, PluginTrustEvaluator.SignatureSidecarFileName),
            payload);
    }

    private sealed class PluginRuntimeFixture : IAsyncDisposable
    {
        private const string PluginIdValue = "plugin.fixture";
        private readonly ServiceProvider _provider;
        private readonly bool _failHealthPing;

        private PluginRuntimeFixture(
            string rootPath,
            ServiceProvider provider,
            bool failHealthPing)
        {
            RootPath = rootPath;
            _provider = provider;
            _failHealthPing = failHealthPing;
        }

        public string RootPath { get; }

        public string PluginId => PluginIdValue;

        public IWorkspaceService Workspace => _provider.GetRequiredService<IWorkspaceService>();

        public IPluginRegistryRepository Registry => _provider.GetRequiredService<IPluginRegistryRepository>();

        public IPluginLifecycleHost Host => _provider.GetRequiredService<IPluginLifecycleHost>();

        public static async Task<PluginRuntimeFixture> CreateAsync(
            bool failHealthPing = false,
            int maxRestartAttempts = 1)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "kodaclaw-runtime-plugin-injection",
                Guid.NewGuid().ToString("N"));

            var services = new ServiceCollection();
            services.AddKodaClawWorkspace(options => options.RootPath = rootPath);
            services.AddKodaClawJsonStore(rootPath);
            services.AddKodaClawControlPlane();
            services.AddKodaClawPluginHost(options =>
            {
                options.MaxRestartAttempts = maxRestartAttempts;
                options.DefaultHealthcheckTimeoutSeconds = 3;
            });

            var provider = services.BuildServiceProvider();
            var fixture = new PluginRuntimeFixture(rootPath, provider, failHealthPing);
            await fixture.InitializeAsync();
            return fixture;
        }

        public async Task ClearActiveMainSessionAsync()
        {
            var appConfig = await Workspace.LoadAppConfigAsync();
            await Workspace.SaveAppConfigAsync(appConfig with { ActiveMainSessionId = null });
        }

        public MainSessionService CreateMainSessionService()
        {
            var registry = new ToolRegistry();
            registry.RegisterBuiltinTools();

            var dependenciesFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = new StubModelProvider(),
                ToolRegistry = registry,
            });

            return new MainSessionService(
                workspaceService: Workspace,
                dependenciesFactory: dependenciesFactory,
                options: new MainSessionOptions
                {
                    Model = "stub-model",
                    MaxIterations = 4,
                },
                pluginRegistryRepository: Registry,
                pluginLifecycleHost: Host);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            DeleteDirectoryWithRetry(RootPath);
        }

        private async Task InitializeAsync()
        {
            await Workspace.EnsureInitializedAsync();

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
                    Environment: environment),
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

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderName => "stub";

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "stub",
            };

            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
            };
        }

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "stub",
                    },
                ],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
                Model = request.Model,
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }
}
