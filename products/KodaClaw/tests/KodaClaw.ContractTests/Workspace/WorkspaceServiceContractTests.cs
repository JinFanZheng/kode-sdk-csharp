using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Workspace;
using System.Text.Json;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class WorkspaceServiceContractTests
{
    [Fact]
    public async Task Ensure_initialized_should_create_expected_workspace_tree()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        var snapshot = await service.EnsureInitializedAsync();
        var entries = Directory
            .EnumerateFileSystemEntries(tempDir.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(tempDir.Path, path).Replace('\\', '/'))
            .OrderBy(path => path)
            .ToArray();

        snapshot.WorkspaceInitialized.Should().BeTrue();
        snapshot.RequiresBootstrap.Should().BeTrue();
        snapshot.DeviceId.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceFingerprintHash.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceAppVersion.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceLastSeenAtUtc.Should().NotBeNullOrWhiteSpace();
        entries.Should().Contain([
            "cache",
            "config",
            "config/app.json",
            "config/gateway.json",
            "identity",
            "identity/device.json",
            "logs",
            "sessions",
            "workspace",
            "workspace/AGENTS.md",
            "workspace/BOOTSTRAP.md",
            "workspace/MEMORY.md",
            "workspace/canvas/index.html",
            "workspace/memory/sessions",
            "workspace/plugins"
        ]);
    }

    [Fact]
    public async Task Ensure_initialized_should_not_overwrite_existing_user_files()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        await service.EnsureInitializedAsync();
        var userFile = Path.Combine(tempDir.Path, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.UserFile);
        await File.WriteAllTextAsync(userFile, "custom-user-profile");

        await service.EnsureInitializedAsync();

        var content = await File.ReadAllTextAsync(userFile);
        content.Should().Be("custom-user-profile");
    }

    [Fact]
    public async Task Save_app_config_should_be_reflected_in_snapshot()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        await service.EnsureInitializedAsync();
        await service.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = "main-001",
        });

        var snapshot = await service.GetSnapshotAsync();

        snapshot.RequiresBootstrap.Should().BeFalse();
        snapshot.ActiveMainSessionId.Should().Be("main-001");
        snapshot.WorkspaceVersion.Should().Be(KodaClawWorkspaceLayout.CurrentWorkspaceVersion);
        snapshot.DeviceFingerprintHash.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceAppVersion.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceLastSeenAtUtc.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Ensure_initialized_should_not_recreate_bootstrap_file_after_bootstrap_completed()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        await service.EnsureInitializedAsync();
        await service.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
        });

        var bootstrapPath = Path.Combine(
            tempDir.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.BootstrapFile);
        var archivedPath = bootstrapPath + ".archived";
        File.Move(bootstrapPath, archivedPath);

        await service.EnsureInitializedAsync();

        File.Exists(bootstrapPath).Should().BeFalse();
        File.Exists(archivedPath).Should().BeTrue();
    }

    [Fact]
    public void Get_session_directory_should_reject_path_traversal()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        var action = () => service.GetSessionDirectory("../escape");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Get_snapshot_should_upgrade_legacy_device_identity_file()
    {
        using var tempDir = new TempDir();
        var service = CreateService(tempDir.Path);

        await service.EnsureInitializedAsync();
        var devicePath = Path.Combine(
            tempDir.Path,
            KodaClawWorkspaceLayout.IdentityDirectory,
            KodaClawWorkspaceLayout.DeviceIdentityFile);

        await File.WriteAllTextAsync(
            devicePath,
            """
            {
              "deviceId": "legacy-device",
              "machineName": "legacy-machine",
              "platform": "legacy-platform",
              "createdAtUtc": "2026-03-19T00:00:00.0000000+00:00"
            }
            """);

        var snapshot = await service.GetSnapshotAsync();

        snapshot.DeviceId.Should().Be("legacy-device");
        snapshot.DeviceFingerprintHash.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceAppVersion.Should().NotBeNullOrWhiteSpace();
        snapshot.DeviceLastSeenAtUtc.Should().NotBeNullOrWhiteSpace();

        var upgraded = JsonSerializer.Deserialize<DeviceIdentity>(
            await File.ReadAllTextAsync(devicePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
            });

        upgraded.Should().NotBeNull();
        upgraded!.FingerprintHash.Should().NotBeNullOrWhiteSpace();
        upgraded.WorkspaceRootHash.Should().NotBeNullOrWhiteSpace();
        upgraded.AppVersion.Should().NotBeNullOrWhiteSpace();
        upgraded.LastSeenAtUtc.Should().NotBeNullOrWhiteSpace();
        upgraded.RotatedAtUtc.Should().BeNull();
        upgraded.RotationReason.Should().BeNull();
    }

    private static WorkspaceService CreateService(string rootPath)
    {
        return new WorkspaceService(new KodaClawWorkspaceOptions
        {
            RootPath = rootPath,
        });
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kodaclaw-workspace-tests", Guid.NewGuid().ToString("N"));
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
