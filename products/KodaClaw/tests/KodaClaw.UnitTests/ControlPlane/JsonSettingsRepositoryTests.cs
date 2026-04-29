using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Settings;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

public sealed class JsonSettingsRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonSettingsRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_return_default_when_no_snapshot()
    {
        var repository = CreateRepository();

        var settings = await repository.GetAsync();

        settings.Should().Be(KodaClawSettings.Default);
    }

    [Fact]
    public async Task Repository_should_persist_and_round_trip_settings()
    {
        var repository = CreateRepository();
        var expected = new KodaClawSettings(
            DefaultLandingRoute: "/home",
            Theme: ThemeMode.Dark,
            RequireApprovalForExternalActions: false,
            NotificationsEnabled: false,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "07:30",
            QuietHoursEndLocalTime: "22:15",
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero));

        await repository.SaveAsync(expected);

        var actual = await repository.GetAsync();

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Repository_should_overwrite_existing_snapshot()
    {
        var repository = CreateRepository();
        var first = KodaClawSettings.Default with
        {
            NotificationsEnabled = true,
            QuietHoursEnabled = true,
            QuietHoursStartLocalTime = "08:00",
            QuietHoursEndLocalTime = "20:00",
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero),
        };
        var second = first with
        {
            NotificationsEnabled = false,
            QuietHoursEnabled = false,
            UpdatedAt = first.UpdatedAt.AddHours(1),
        };

        await repository.SaveAsync(first);
        await repository.SaveAsync(second);

        var actual = await repository.GetAsync();

        actual.Should().Be(second);
    }

}
