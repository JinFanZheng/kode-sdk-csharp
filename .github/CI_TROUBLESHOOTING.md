# CI/CD 故障排查指南

本文档记录了 GitHub Actions CI/CD 流程中遇到的问题和解决方案。

## 已解决的问题

### 1. .NET 10.0 预览版支持问题

**问题描述**:
- GitHub Actions 构建失败，提示找不到 .NET 10.0 SDK
- 错误: "Process completed with exit code 1"

**根本原因**:
- .NET 10.0 是预览版本
- GitHub Actions 的 `setup-dotnet@v4` 默认只安装稳定版本

**解决方案**:
在 `.github/workflows/*.yml` 中添加预览版支持：

```yaml
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '10.0.x'
    dotnet-quality: 'preview'  # 关键：启用预览版支持
```

**相关 Commit**: `0982500`

---

### 2. 跨平台测试兼容性问题

**问题描述**:
- Windows 平台测试失败
- 使用了 Unix 特定的 shell 命令 (`ls -la`, `echo $VAR`)

**根本原因**:
- 测试代码包含平台特定的 shell 命令
- 没有跨平台的测试支持

**解决方案**:

#### A. 创建平台特定测试属性
```csharp
[UnixOnlyFact]  // 仅在 Linux/macOS 运行
public async Task CommandExecution_WithWorkingDirectory() { ... }

[WindowsOnlyFact]  // 仅在 Windows 运行
public async Task CommandExecution_OnWindows() { ... }
```

#### B. 使用跨平台命令辅助工具
```csharp
// 自动适配平台
var cmd = PlatformCommands.Echo("Hello");
var result = await sandbox.ExecuteCommandAsync(cmd);
```

**修改文件**:
- 新增: `tests/Kode.Agent.Tests/Helpers/PlatformFact.cs`
- 修改: `LocalSandboxTests.cs`, `SandboxFileSystemIntegrationTests.cs`

**相关 Commit**: `7152244`

---

### 3. GitHub Release 创建权限问题

**问题描述**:
- 发布工作流失败："Too many retries"
- Release 创建步骤无法完成

**根本原因**:
- 缺少 `contents: write` 权限
- GitHub Actions 默认 `GITHUB_TOKEN` 权限受限

**解决方案**:
在 `publish-nuget.yml` 中添加权限声明：

```yaml
jobs:
  publish:
    runs-on: ubuntu-latest
    permissions:
      contents: write  # 允许创建 Release
      packages: write  # 允许发布包
```

升级 Release Action：
```yaml
- name: Create GitHub Release
  uses: softprops/action-gh-release@v2  # v1 → v2
  if: startsWith(github.ref, 'refs/tags/')
  with:
    files: |
      ./nupkgs/*.nupkg
      ./nupkgs/*.snupkg
    generate_release_notes: true
    fail_on_unmatched_files: false
```

**相关 Commit**: `7152244`

---

### 4. 异步测试时序问题

**问题描述**:
- CI 环境下偶发测试失败
- 本地测试通过，CI 失败
- 错误: "Expected at least 1 event, got 0"

**根本原因**:
- CI 环境资源受限，执行速度慢
- 固定延迟时间不够
- 缺少可靠的同步机制

**解决方案**:

#### 修改前 (不可靠):
```csharp
await Task.Delay(50);  // 固定延迟
_eventBus.EmitProgress(...);
await Task.WhenAny(subscribeTask, Task.Delay(1000));
```

#### 修改后 (可靠):
```csharp
var tcs = new TaskCompletionSource<bool>();
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

await Task.Delay(200);  // 增加初始等待时间
_eventBus.EmitProgress(...);

// 使用 TaskCompletionSource 进行显式信号
tcs.TrySetResult(true);

// 等待信号或超时
await Task.WhenAny(tcs.Task, Task.Delay(3000));

// 确保清理
try { await subscribeTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
```

**改进点**:
- ✅ 增加超时时间 (2s → 5s)
- ✅ 使用 `TaskCompletionSource` 进行显式同步
- ✅ 增加订阅启动延迟 (100ms → 200ms)
- ✅ 确保任务正确清理

**相关 Commit**: 最新提交

---

## CI 环境特点

### GitHub Actions Runners 规格

| 平台 | CPU | 内存 | 磁盘 |
|------|-----|------|------|
| ubuntu-latest | 4 核 | 16 GB | 14 GB SSD |
| windows-latest | 4 核 | 16 GB | 14 GB SSD |
| macos-latest | 3 核 | 14 GB | 14 GB SSD |

### CI 环境 vs 本地环境差异

| 特性 | 本地环境 | CI 环境 |
|------|---------|---------|
| CPU 性能 | 高 | 中等（共享） |
| 网络延迟 | 低 | 可能较高 |
| 文件系统 | 快速 | 可能较慢 |
| 资源竞争 | 少 | 多（其他作业） |
| 时间稳定性 | 高 | 较低 |

---

## 最佳实践

### 1. 编写可靠的异步测试

❌ **不好**:
```csharp
await Task.Delay(100);  // 固定延迟，不可靠
Assert.True(someFlag);
```

✅ **好**:
```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var tcs = new TaskCompletionSource<bool>();

// 使用信号量或 TaskCompletionSource
someEvent += () => tcs.TrySetResult(true);

await tcs.Task.WaitAsync(cts.Token);
```

### 2. 跨平台测试

❌ **不好**:
```csharp
await sandbox.ExecuteCommandAsync("ls -la");  // Unix 特定
```

✅ **好**:
```csharp
[UnixOnlyFact]  // 明确标记平台要求
public async Task UnixSpecificTest()
{
    await sandbox.ExecuteCommandAsync("ls -la");
}

// 或使用跨平台辅助工具
await sandbox.ExecuteCommandAsync(PlatformCommands.ListDirectory);
```

### 3. 超时配置

建议超时时间：
- 单元测试: 1-2 秒
- 集成测试: 5-10 秒
- CI 环境: 本地时间 × 2-3

### 4. 资源清理

确保测试后清理资源：
```csharp
public async ValueTask DisposeAsync()
{
    try
    {
        await _sandbox.DisposeAsync();
        Directory.Delete(_testDir, recursive: true);
    }
    catch { }  // 忽略清理错误
}
```

---

## 调试 CI 失败

### 1. 查看详细日志

```bash
# 在 GitHub Actions 中启用详细日志
# 设置 Repository Secret:
# ACTIONS_STEP_DEBUG = true
```

### 2. 本地重现 CI 环境

```bash
# 使用 act 在本地运行 GitHub Actions
brew install act
act -j build  # 运行特定作业
```

### 3. 增加诊断输出

```csharp
[Fact]
public async Task DebugTest()
{
    Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
    Console.WriteLine($"Platform: {RuntimeInformation.RuntimeIdentifier}");
    Console.WriteLine($"CPU: {Environment.ProcessorCount}");
    
    // 测试代码...
}
```

---

## 检查清单

在提交 PR 前确认：

- [ ] 所有测试在本地通过（Debug 和 Release）
- [ ] 跨平台测试正确标记（`[UnixOnlyFact]` / `[WindowsOnlyFact]`）
- [ ] 异步测试使用了可靠的同步机制
- [ ] 超时时间足够宽松（考虑 CI 环境）
- [ ] 资源正确清理（实现 `IAsyncDisposable`）
- [ ] 工作流文件语法正确（本地验证 YAML）

---

## 参考资源

- [GitHub Actions 文档](https://docs.github.com/en/actions)
- [xUnit 文档](https://xunit.net/docs/getting-started/netcore/cmdline)
- [.NET 测试最佳实践](https://docs.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)
- [跨平台开发指南](https://docs.microsoft.com/en-us/dotnet/core/compatibility/)
