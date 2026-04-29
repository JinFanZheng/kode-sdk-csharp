using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.UnitTests.Gateway;

/// <summary>
/// L1 单元测试 — SessionRetentionService
/// 验证 auto-* session 文件夹的保留/删除逻辑（按任务分组、最大数量、天数截止）。
/// </summary>
public sealed class SessionRetentionServiceTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _sessionsRoot;

    public SessionRetentionServiceTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "kodaclaw-retention-tests", Guid.NewGuid().ToString("N"));
        _sessionsRoot = Path.Combine(_workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        Directory.CreateDirectory(_sessionsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 无 auto- 文件夹时提前退出
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_be_noop_when_no_auto_sessions_exist()
    {
        CreateCompletedSession("main-20260320120000-aabbccdd");

        var service = BuildService(retentionDays: 30, maxPerTask: 20);
        await service.RunAsync();

        Directory.GetDirectories(_sessionsRoot).Should().HaveCount(1, "main- folder must not be touched");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 按任务分组 — 超出 maxPerTask 的旧记录被删除
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_delete_oldest_sessions_when_count_exceeds_maxPerTask()
    {
        // 3 次运行，maxPerTask = 2 → 最旧的第 3 次应被删除
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1);
        CreateCompletedSession("auto-20260324120000-daily-check-aaaa0001", recentDate);
        CreateCompletedSession("auto-20260323120000-daily-check-aaaa0002", recentDate.AddDays(-1));
        CreateCompletedSession("auto-20260322120000-daily-check-aaaa0003", recentDate.AddDays(-2));

        var service = BuildService(retentionDays: 30, maxPerTask: 2);
        await service.RunAsync();

        var remaining = Directory.GetDirectories(_sessionsRoot).Select(Path.GetFileName).ToList();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain("auto-20260324120000-daily-check-aaaa0001");
        remaining.Should().Contain("auto-20260323120000-daily-check-aaaa0002");
        remaining.Should().NotContain("auto-20260322120000-daily-check-aaaa0003");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 按天数截止 — 超出 retentionDays 的记录被删除
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_delete_sessions_older_than_retentionDays()
    {
        var now = DateTimeOffset.UtcNow;
        CreateCompletedSession("auto-20260324120000-weekly-report-bbbb0001", now.AddDays(-5));
        CreateCompletedSession("auto-20260310120000-weekly-report-bbbb0002", now.AddDays(-35)); // 超过 30 天

        var service = BuildService(retentionDays: 30, maxPerTask: 20);
        await service.RunAsync();

        var remaining = Directory.GetDirectories(_sessionsRoot).Select(Path.GetFileName).ToList();
        remaining.Should().ContainSingle();
        remaining.Should().Contain("auto-20260324120000-weekly-report-bbbb0001");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 两个规则都满足才保留（AND 语义）
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_delete_sessions_exceeding_either_count_or_days_limit()
    {
        var now = DateTimeOffset.UtcNow;
        // session1: 在数量内 + 在天数内 → 保留
        CreateCompletedSession("auto-20260324120000-task-x-cccc0001", now.AddDays(-1));
        // session2: 在数量内 但 超天数 → 删除
        CreateCompletedSession("auto-20260220120000-task-x-cccc0002", now.AddDays(-40));

        var service = BuildService(retentionDays: 30, maxPerTask: 5);
        await service.RunAsync();

        var remaining = Directory.GetDirectories(_sessionsRoot).Select(Path.GetFileName).ToList();
        remaining.Should().ContainSingle();
        remaining[0].Should().Be("auto-20260324120000-task-x-cccc0001");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 跳过没有 meta.json 的文件夹（正在执行中）
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_skip_folders_without_meta_json()
    {
        // 这个文件夹没有 meta.json（模拟正在执行中的 session）
        var runningDir = Path.Combine(_sessionsRoot, "auto-20260320120000-in-flight-dddd0001");
        Directory.CreateDirectory(runningDir);

        var service = BuildService(retentionDays: 1, maxPerTask: 1); // 极短保留期
        await service.RunAsync();

        Directory.Exists(runningDir).Should().BeTrue("进行中的 session 不得被删除");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 按任务 ID 分组 — 不同 taskId 的配额互不影响
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_group_sessions_independently_by_task_id()
    {
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1);
        // task-a 3 次，maxPerTask = 2 → 最旧删 1 次
        CreateCompletedSession("auto-20260324120000-task-a-eeee0001", recentDate);
        CreateCompletedSession("auto-20260323120000-task-a-eeee0002", recentDate.AddDays(-1));
        CreateCompletedSession("auto-20260322120000-task-a-eeee0003", recentDate.AddDays(-2));
        // task-b 2 次，maxPerTask = 2 → 全部保留
        CreateCompletedSession("auto-20260324120000-task-b-ffff0001", recentDate);
        CreateCompletedSession("auto-20260323120000-task-b-ffff0002", recentDate.AddDays(-1));

        var service = BuildService(retentionDays: 30, maxPerTask: 2);
        await service.RunAsync();

        var remaining = Directory.GetDirectories(_sessionsRoot).Select(Path.GetFileName).ToList();
        remaining.Should().HaveCount(4);
        remaining.Should().NotContain("auto-20260322120000-task-a-eeee0003");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // main- 和 channel- 文件夹不被触碰
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_never_touch_main_or_channel_session_folders()
    {
        CreateCompletedSession("main-20260101120000-aabb0001", DateTimeOffset.UtcNow.AddDays(-60));
        CreateCompletedSession("channel-dm-binding-0001", DateTimeOffset.UtcNow.AddDays(-60));

        var service = BuildService(retentionDays: 1, maxPerTask: 1);
        await service.RunAsync();

        Directory.Exists(Path.Combine(_sessionsRoot, "main-20260101120000-aabb0001")).Should().BeTrue();
        Directory.Exists(Path.Combine(_sessionsRoot, "channel-dm-binding-0001")).Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 降级路径：meta.json 的 createdAt 无效时，从文件夹名解析时间戳
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_should_fall_back_to_folder_name_timestamp_when_createdAt_missing()
    {
        // 文件夹名时间戳 = 2026-01-01（超过 30 天），meta.json 有效但 createdAt 字段缺失
        var dir = Path.Combine(_sessionsRoot, "auto-20260101120000-fallback-gggg0001");
        Directory.CreateDirectory(dir);
        // meta.json 不含 createdAt 字段
        await File.WriteAllTextAsync(Path.Combine(dir, "meta.json"), """{"agentId":"fallback-gggg0001"}""");

        var service = BuildService(retentionDays: 30, maxPerTask: 20);
        await service.RunAsync();

        Directory.Exists(dir).Should().BeFalse("文件夹名时间戳超出 retentionDays 应被删除");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void CreateCompletedSession(string name, DateTimeOffset? createdAt = null)
    {
        var dir = Path.Combine(_sessionsRoot, name);
        Directory.CreateDirectory(dir);

        // 写 meta.json（模拟 AgentInfo）
        var ts = createdAt ?? DateTimeOffset.UtcNow.AddDays(-1);
        var meta = new { agentId = name, createdAt = ts.ToString("O") };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
    }

    private SessionRetentionService BuildService(int retentionDays, int maxPerTask)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = _workspaceRoot);
        using var provider = services.BuildServiceProvider();

        var workspaceService = provider.GetRequiredService<IWorkspaceService>();

        // 同步初始化 workspace 并设置 config
        workspaceService.EnsureInitializedAsync().GetAwaiter().GetResult();
        workspaceService.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            AutoSessionRetentionDays = retentionDays,
            AutoSessionRetentionMaxPerTask = maxPerTask,
        }).GetAwaiter().GetResult();

        return new SessionRetentionService(workspaceService);
    }
}
