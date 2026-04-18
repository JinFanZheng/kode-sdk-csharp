# Agent.cs 拆分路线图

> 本文档记录 `src/Kode.Agent.Sdk/Core/Agent/Agent.cs`（当前 3,901 行）的拆分规划，目标：在**零行为变更、零 API 变更、零回归**的前提下提升可维护性。

## 背景

Agent.cs 是 god-object，承担了：公共 API 门面 / 主循环 / 模型流式 / 工具调度 / 状态恢复 / 状态持久化 / 子 Agent 派发 / Skills 激活 / Bootstrap 初始化 / Tool-Call 清理 / Template 应用 共 ~11 类职责。3,901 行单文件是当前 SDK 最大技术债。

## 约束

1. **公共 API 签名** — `IAgent`/`Agent` 所有 `public` 成员签名字节级等价
2. **事件流** — `EventBus.EmitProgress/EmitMonitor/EmitControl` 的顺序、类型、payload 完全一致
3. **状态持久化格式** — `BreakpointState`、`AgentInfo`、metadata JSON schema 完全一致
4. **Thread-safety** — `_stateLock`/`_processingLock`/`_activeToolCallsLock` 保护关系不变
5. **生命周期** — `Ready → Working → Paused/Ready` 转换时机不变
6. **错误分类** — `ClassifyModelError`/`ClassifyToolCategory` 返回字符串逐字节等价
7. **度量** — `KodeAgentMetrics.StepsCompleted/StepDuration/ContextCompressions` 触发点不变

---

## 四阶段路线图

### Phase 0 — Partial-class 拆分（1 个 PR，0 风险）

把 `Agent.cs` 改为 `partial sealed class`，按逻辑块拆到多个 `.cs` 文件。**不提取协调器、不新增接口、不动字段、不动方法签名**。

C# `partial` 在编译期重新组合为同一个 type，字节码等价，零回归风险。

| 新文件 | 内容 | 行数估算 |
|---|---|---|
| `Agent.cs` | 字段 (L22–L107) + 私有 ctor (L108–L198) + 公共只读属性 | ~200 |
| `Agent.Lifecycle.cs` | `RunAsync` ×3, `Send` ×2, `SendAsync`, `PauseAsync`, `ResumeAsync`, `ApproveToolCallAsync`, `DenyToolCallAsync`, `InterruptAsync`, `Kick`, `DisposeAsync` | ~600 |
| `Agent.Step.cs` | `StepAsync` (L1146–L1446) | ~300 |
| `Agent.ModelStreaming.cs` | `BuildModelRequest`, `StreamModelResponseAsync` | ~385 |
| `Agent.ToolDispatch.cs` | `ProcessToolCallsAsync`, `ClassifyToolCategory`, `ComputeContextPressure`, `GetSnapshotOrFallback` | ~410 |
| `Agent.StateSanitizer.cs` | `BuildSealPayload`, `SealNonTerminalToolRecords`, `AutoSealDanglingToolUsesAsync`, `SanitizeOrphanToolResultsAsync`, `SanitizeDanglingUserTurns`, `PreviewToolResult` | ~280 |
| `Agent.Persistence.cs` | `SaveStateAsync` ×2, `UpdateInfoAsync` ×2, `BuildAgentMetadata`, `TransitionState`, `NowMs`, `TouchProcessingHeartbeat`, `FindLastSfpIndex` | ~230 |
| `Agent.Processing.cs` | `EnqueueMessageAsync`, `EnsureProcessing` | ~185 |
| `Agent.Skills.cs` | `ActivateSkillAsync`, `InitializeSkillsAsync` | ~110 |
| `Agent.SubAgent.cs` | `DelegateTaskAsync`, `SpawnSubAgentAsync`, `ForwardSubAgentEventsAsync`, `ConvertPermissionConfig` | ~345 |
| `Agent.Bootstrap.cs` | `LoadTools`, `InitializeToolServices`, `InitializeTodoAsync`, `InitializeToolManualAsync`, `RenderToolManual`, `HandleExternalFileChange`, `RemindAsync`, `WrapReminder`, `BuildInputPreview`, `ParseChannels` | ~320 |
| `Agent.StateRecovery.cs` | `CreateAsync`, `ResumeFromStoreAsync` ×2, `ResumeFromStoreInternalAsync`, `BuildResumeConfigFromInfo`, `ApplyResumeOverrides`, `ReadString/Bool/Int/Double/Object`, `ReadToolIds`, `ReadSandboxOptionsFromSandboxConfig`, `ReadToolDescriptors`, `TryReadInt`, `StatusAsync`, `InfoAsync` | ~420 |
| `Agent.Templates.cs` | `ApplyTemplateConfig`, `ConvertSandboxOptions`, `RegisterHooks` | ~200 |
| `Agent.Subscriptions.cs` | `Subscribe`, `SubscribeProgress`, `On`, `Schedule`, `ChatStream`, `ChatStreamAsync`, `ChatAsync`, `CompleteAsync`, `Stream`, `SnapshotAsync`, `ForkAsync`, `GetTodosAsync`, `SetTodosAsync`, `GetHistoryWindowsAsync`, `ForceCompressAsync`, 嵌套类型（`CancellationDisposable`, `SubscribeOptions`, `StreamOptions`, `CompleteResult`, `SealPayload`, `NextModelToolsMode/Override`, `ToolServices`, `ClassifyModelError`） | ~700 |

总计 14 个 partial 文件，单文件 110–700 行。

**关键约束**：不拆嵌套类型到独立文件，随用到它的 partial 走即可。

---

### Phase 1 — 纯静态工具类（3–5 个 PR，低风险）

Phase 0 落地后才启动。每类都是纯 static、无状态、零副作用。

| 新类（位置） | 从哪个 partial 搬 | 迁移内容 |
|---|---|---|
| `AgentMetadataReader` (`Core/Agent/Internal/`) | `Agent.StateRecovery.cs` | `ReadString/ReadBool/ReadInt/ReadDouble/ReadObject/ReadToolIds/ReadSandboxOptionsFromSandboxConfig/ReadToolDescriptors/TryReadInt` |
| `AgentTemplateApplicator` (static) | `Agent.Templates.cs` | `ApplyTemplateConfig/ConvertSandboxOptions/ConvertPermissionConfig/RegisterHooks` |
| `AgentDiagnosticsHelpers` (static) | `Agent.ToolDispatch.cs`, `Agent.Subscriptions.cs` | `ClassifyModelError/ClassifyToolCategory/PreviewToolResult/BuildInputPreview/ParseChannels` |

**回归门**：每个迁移做 L0（build）+ 既有 xUnit 单测跑通。**禁止**改方法签名/行为。

---

### Phase 2 — 无状态协调器（风险中等）

不持有 `_messages` 引用，通过 Agent 传进来的参数工作，作为 sealed class 提取。

| 新协调器 | 从 partial 迁出 | 依赖面 |
|---|---|---|
| `AgentStateRecoveryCoordinator` | `Agent.StateRecovery.cs` 的 `CreateAsync` + `ResumeFromStoreAsync` ×2 + 内部助手 | 静态工厂路径，不涉及 Agent 实例状态 |
| `ToolCallStateSanitizer` | `Agent.StateSanitizer.cs` | 接收 `List<Message>`/`EventBus`/`HookManager` 作为 ctor 参数 |
| `AgentBootstrapCoordinator` | `Agent.Bootstrap.cs` | 接收 dependencies + config，只在 ctor/resume 时触发 |

**回归门**：L1 单元 + 现有集成测试全绿。每个协调器独立 PR。

---

### Phase 3 — 主循环协调器（高风险，留到最后）

Phase 0/1/2 让 Agent.cs 从 3,901 行降到 ~2,500 行。此时再考虑是否提取 `AgentRunLoop` + `ModelStreamingCoordinator` + `ToolDispatchCoordinator`。

**提取策略**：每个协调器接收一个 `AgentRunContext` 记录，暴露**只读快照 + 回调**，避免共享可变引用导致的锁竞争。

```csharp
internal sealed record AgentRunContext(
    EventBus EventBus,
    BreakpointManager Breakpoint,
    HookManager Hooks,
    ContextManager Context,
    PermissionManager Permissions,
    ToolRunner ToolRunner,
    MessageQueue Queue,
    IReadOnlyList<Message> Messages,
    Action<Message> AppendMessage,
    Action ClearMessages,
    Action<IEnumerable<Message>> ReplaceMessages,
    Func<CancellationToken, Task> SaveState
);
```

**必须前置**：Phase 3 前必须加一轮 golden 测试（录制一段典型 chat 的 event stream，保存为 JSONL，重构后重放比对严格相等），否则不启动。

---

## 验证矩阵

| 阶段 | L0 (build) | L1 (unit) | L2 (integration) | Golden event stream | 人工 Dogfood |
|---|---|---|---|---|---|
| Phase 0 | ✅ 必过 | ✅ 全量 | ✅ 全量 | — | — |
| Phase 1 | ✅ 必过 | ✅ 全量 | ✅ 全量 | — | — |
| Phase 2 | ✅ 必过 | ✅ 全量 + 新增协调器单测 | ✅ 全量 | — | 1 次小 Dogfood |
| Phase 3 | ✅ 必过 | ✅ 全量 + 新增协调器单测 | ✅ 全量 | ✅ **必过** | ✅ 必过 |

---

## 回滚策略

- 每阶段单独 PR，使用 feature branch（如 `refactor/agent-partial-split`）
- 任何 L2 集成测试回归即 `git revert`
- Phase 3 前加 feature flag (`AgentConfig.UseLegacyRunLoop = true/false`)，默认 legacy，稳定后再翻默认值

---

## 估算与收益

| 阶段 | 工作量 | Agent.cs 终态行数 | 每 partial 文件行数 |
|---|---|---|---|
| Phase 0 | 1 PR，1–2 天 | 3,901 → 14 个 partial，每个 110–700 | 平均 280 |
| Phase 1 | 3–5 PR，2–3 天 | partial 再减 ~500 | — |
| Phase 2 | 3 PR，1 周 | partial 再减 ~800 | — |
| Phase 3（可选） | 3 PR，2 周 + 测试 | Agent.cs 最终 ≤ 1,000 行 | — |

---

## 附录：角色接口核查（Option A）

| 接口 | 使用点 | 结论 |
|---|---|---|
| `ISkillsAwareAgent` | **真窄化**。`SkillListTool`/`SkillActivateTool`/`SkillResourceTool` 都用 `context.Agent is not ISkillsAwareAgent skillsAware` 做能力门禁 | ✅ 保留 |
| `ITaskDelegatorAgent` | **真窄化**。`TaskRunTool.cs:119` 用 `context.Agent is not ITaskDelegatorAgent delegator` 门禁 task_run 工具 | ✅ 保留 |
| `ISubAgentSpawnerAgent` | **死代码**。只在 `Agent.cs:20` 声明实现 + `Agent.cs:3101` 定义 `SpawnSubAgentAsync` — 全仓库 0 个调用点 | ⚠️ 可移除或合并进 `ITaskDelegatorAgent`（新增 `Runtime: SubAgentRuntime?` 字段） |

`SubAgentRuntime` record 里的 `DepthRemaining` 同样未被消费；递归深度控制当前经过 `AgentConfig`/Template 层。
