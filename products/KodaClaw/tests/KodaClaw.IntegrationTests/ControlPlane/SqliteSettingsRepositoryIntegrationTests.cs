using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Settings;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ControlPlane;

public sealed class SqliteSettingsRepositoryIntegrationTests
{
    [Fact]
    public async Task Service_registration_should_persist_settings_across_service_provider_restarts()
    {
        using var workspace = new TempWorkspaceRoot();
        var expected = new KodaClawSettings(
            DefaultLandingRoute: "/config",
            Theme: ThemeMode.Dark,
            RequireApprovalForExternalActions: false,
            NotificationsEnabled: false,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "06:00",
            QuietHoursEndLocalTime: "23:00",
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 15, 0, 0, TimeSpan.Zero));

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<ISettingsRepository>();
            await repository.SaveAsync(expected);
        }

        using (var provider = CreateServiceProvider(workspace.Path))
        {
            var repository = provider.GetRequiredService<ISettingsRepository>();
            var actual = await repository.GetAsync();

            actual.Should().Be(expected);
        }
    }

    [Fact]
    public async Task Service_registration_should_return_default_when_no_snapshot()
    {
        using var workspace = new TempWorkspaceRoot();

        using var provider = CreateServiceProvider(workspace.Path);
        var repository = provider.GetRequiredService<ISettingsRepository>();

        var actual = await repository.GetAsync();

        actual.Should().Be(KodaClawSettings.Default);
    }

    private static ServiceProvider CreateServiceProvider(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        return services.BuildServiceProvider();
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-settings-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
