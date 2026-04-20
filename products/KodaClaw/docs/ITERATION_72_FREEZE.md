# Iteration 72 FREEZE — Channel 实时进度指示器（Progress Indicator）

> 冻结日期：2026-04-20
> 状态：FROZEN

---

## 背景与动机

当前 Channel（Telegram/飞书/钉钉/微信）侧用户在长 turn 中看不到进度：

- `FormatSessionStatusMessage`（`src/KodaClaw.ChannelHub/Commands/ChannelCommandDispatcher.cs:460`）只在用户发 `/s` 时被动触发
- 已有 `ChannelProgressStreamer`（`src/KodaClaw.ChannelHub/Turn/ChannelProgressStreamer.cs`）仅 flush "思考文本"，不反映 `BreakpointState` / `StepCount` 这类状态级进度
- `IChannelConnector.SendAsync` 没有返回 `ExternalMessageId`，即便底层 `HttpTelegramApiClient.SendMessageAsync` 有返回 `message_id` 也被上层丢弃 → 当前链路每次都发新消息，频繁推送必然刷屏

本迭代让支持 edit 的平台（Telegram、飞书）在同一条"🔄 正在处理..."消息里随 Agent 状态实时滚动更新；不支持 edit 的平台（钉钉、微信、Webhook/Relay）静默降级，不刷屏。

---

## 目标（用户路径）

1. 用户在飞书/Telegram 发一个耗时消息 → 2s 内收到 "🔄 正在处理..." 初始消息
2. Agent 调 `fs_read` → 该消息被**编辑**为 "🔄 读取文件 (fs_read) · 第 3 步"
3. Agent 进入 `AwaitingApproval` → 编辑为 "⏸ 等待审批 (bash_run)"
4. turn 结束 → 进度消息被编辑为 "✓ 完成 · 12 步 / 43s"，**最终回复通过 `channel_send` 另发一条**
5. 同场景在微信/钉钉 → 不出现进度消息；最终回复正常到达，不刷屏

---

## 范围（IN SCOPE）

### 契约 — `KodaClaw.Contracts`

- `IChannelConnector` 新增（均带默认实现，向后兼容）：
  - `bool SupportsEdit => false;`
  - `Task<ChannelSendReceipt> SendWithReceiptAsync(ChannelOutboundDraft, CancellationToken)` — 默认委托老 `SendAsync` 并返回 `ChannelSendReceipt(null, DateTimeOffset.UtcNow)`
  - `Task EditAsync(string externalThreadId, string externalMessageId, string text, OutboundMessageFormat format, CancellationToken)` — 默认 `throw new NotSupportedException()`
- 新增 record：`ChannelSendReceipt(string? ExternalMessageId, DateTimeOffset SentAt)`（放在 `src/KodaClaw.Contracts/Channels/ChannelSendReceipt.cs`）
- `ChannelAccountSettings`（JSON schema，文档化，不强类型化）扩展字段：
  ```json
  {"progressIndicator": {"enabled": true, "style": "verbose"}}
  ```

### 后端 — `KodaClaw.ChannelHub`

- **新** `Turn/ChannelProgressIndicator.cs`：独立于 `ChannelProgressStreamer`，订阅 Monitor 频道 `breakpoint_changed` + Progress 频道 `tool:start` / `tool:end` / `done`，按节流规则合并后调 edit
- `ChannelDeliveryDispatchService` 新增：
  - `Task<ChannelSendReceipt> SendProgressInitialAsync(ChannelAccount, ThreadBinding, string text, CT)` — 发首条进度消息并返回 receipt（best-effort，失败返回 `ChannelSendReceipt(null, ...)`）
  - `Task EditProgressAsync(ChannelAccount, ThreadBinding, string externalMessageId, string text, CT)` — best-effort，失败不 throw
- `ChannelTurnOrchestrator`：
  - Turn 开始时根据 `account.Settings.progressIndicator.enabled && connector.SupportsEdit` 决定是否启用 indicator；与现有 `ChannelProgressStreamer` **互斥**（见决策 3）
  - DoneEvent 到达时，indicator 把进度消息编辑为 "✓ 完成 · {StepCount} 步 / {elapsed}"；最终回复（`channel_send` 或 fallback RawResponse）走正常 `SendNotificationAsync` 路径，**另发一条**
- `TelegramConnector`：`SupportsEdit = true` + `EditAsync` → 调 `HttpTelegramApiClient.EditMessageTextAsync`（新增，走 `editMessageText` API）
- `FeishuConnector`：`SupportsEdit = true` + `EditAsync` → 调 `HttpFeishuApiClient.PatchMessageAsync`（新增，走 `PATCH /open-apis/im/v1/messages/{id}`）
- 其他 Connectors（`DingTalkConnector` / `WeChatConnector` / `GenericWebhookConnector` / `RelayConnector`）：保留 `SupportsEdit = false` 默认，无需改动

### 前端 — `apps/kodaclaw-web`

- `src/types/contracts.ts`：`ChannelAccountSettings` 加可选 `progressIndicator?: { enabled: boolean; style?: "verbose" | "compact" }`
- `src/shell/desks/ChannelsDesk.tsx`：账号卡片在 `connectorKind ∈ {Telegram, Feishu}` 时显示"进度指示"开关 + style 选择；默认 `enabled=true, style="verbose"`；读写走现有 `PUT /api/channels/accounts/{id}` 端点

---

## 非目标（OUT OF SCOPE）

- **Chat UI**：已由 Iter 71 覆盖（Sub-Agent 进度），本迭代不动 Chat
- **Automation session**：后台任务不面向 Channel 用户，无进度需求
- **跨 Turn 编辑**：一 Turn 一条进度消息，DoneEvent 封顶，下一 Turn 重开；不复用前一 Turn 的 `externalMessageId`
- **进度消息的删除**：仅支持编辑（Telegram/飞书 deleteMessage 能力二期再议）
- **钉钉 / 微信个人号 edit 支持**：微信个人号协议层无编辑能力；钉钉机器人消息通常不支持编辑。本迭代不做"新消息刷屏"替代方案
- **Thinking 文本保留 + indicator 共存**：indicator 开启时 `ChannelProgressStreamer` 禁用，避免双路推送叠加（详见决策 3）
- **deleteMessage 终态方案**：`DoneEvent` 时不删除进度消息，保留为"过程回放"

---

## 关键设计决策

### 1. 事件源：Monitor `BreakpointChangedEvent` 为主，Progress `tool:start/end` 为辅

`BreakpointChangedEvent`（`BreakpointManager.cs:41` 发出）在每次状态切换时触发，是最准的进度信号。但它不带"当前工具名"，工具名从 `ToolStartEvent.Call.Name` 拿（`ToolEndEvent` 时清空缓存）。`StepCount` 从 `BreakpointChangedEvent.StepCount` 拿。`elapsed` 在 indicator 本地用 `DateTimeOffset.UtcNow - turnStartedAt` 计算。

订阅方式参考 Iter 71：`System.Threading.Channels` fan-in Progress + Monitor 两路，由 Indicator 主循环消费。**退出与 CTS 责任与 Iter 71 一致**：`using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)`；两个后台 Task 使用 `linkedCts.Token`；主循环在 `DoneEvent` / 异常后的 `finally` 块中调 `await linkedCts.CancelAsync()` + `await Task.WhenAll(progressTask, monitorTask)`，避免 Monitor 后台 Task 悬挂到外层 `cancellationToken` 超时。

### 2. 节流规则

- **min-interval 1.5s**：两次 edit 至少间隔 1.5s（Telegram ≈1 req/s 软限，飞书更严）
- **状态去重**：相同 `(BreakpointState, ToolName, StepCount)` 三元组不重画
- **同状态心跳 10s**：长 tool 期间即使无状态切换，最多每 10s 一次 "💭 仍在 {tool}..." 的微调（只改 elapsed 秒数），避免用户误判卡死
- **每 turn edit 上限 10 次**：超过后 indicator 静默，只保留终态编辑（DoneEvent 仍会编辑一次 "✓ 完成"）
- **合并 coalescing**：节流窗口内只保留最新状态，不排队堆积

### 3. 与现有 `ChannelProgressStreamer` 互斥

indicator 开启时 `ChannelProgressStreamer` 不订阅。原因：
- 两者都向同一 thread 推送消息，叠加会刷屏
- indicator 已覆盖"进度可见性"语义，Streamer 的"思考文本 flush"在 indicator 场景冗余

`ChannelTurnOrchestrator` 选路逻辑（伪码）：
```csharp
var effectiveIndicator =
    turnContext.EnableProgressIndicatorOverride
    ?? (ReadSetting(account, "progressIndicator.enabled") && connector.SupportsEdit);

if (effectiveIndicator)
{
    // 启动 ChannelProgressIndicator，禁用 Streamer
}
else if (_sessionOptions.EnableProgressStreaming || turnContext.EnableProgressStreamingOverride == true)
{
    // 走现有 ChannelProgressStreamer 路径（不变）
}
else
{
    // 不订阅，仅 DoneEvent 后的 fallback 投递
}
```

### 4. 终态处理：编辑进度消息为 "✓ 完成"，最终回复另发

`DoneEvent` 到达时：
- indicator 将进度消息编辑为 `✓ 完成 · {StepCount} 步 / {elapsed}`（保留作为"过程回放"）
- 最终回复走正常路径：
  - Agent 调用了 `channel_send` → `channel_send` 工具自己投递（现有行为不变）
  - Agent 未调 `channel_send` 但有 `RawResponse` → Orchestrator fallback 调 `SendNotificationAsync(rawResponse)`（现有行为不变）
  - 两种情况都不与 indicator 编辑冲突，用户看到两条消息：一条 ✓ 完成摘要 + 一条 final reply

**不做消息合并优化**（用户已选"final reply 另发"策略）。语义清晰度优于消息数量。

### 5. 失败回退：连续 2 次 edit 失败 → 关闭 indicator

EditAsync 的 HTTP 错误、rate-limit、message not found 全部归入失败。连续 2 次失败后 indicator `_degraded = true`，Turn 剩余期间不再 edit，终态也跳过。诊断事件：`channel.progress_indicator.degraded`，level=warn。

首条 `SendProgressInitialAsync` 失败（返回 `ChannelSendReceipt(null, ...)`）时直接走 degraded，不尝试 edit。

### 6. AwaitingApproval 语义

`BreakpointState.AwaitingApproval` 到达时：
- indicator 编辑为 `⏸ 等待审批 · {toolName}`
- 同时现有审批流程会通过 `SendNotificationAsync` 发独立的审批消息（`ChannelDeliveryApprovalService` 行为不变，含 token 与操作提示）
- 用户回复 `ok xxxxx` 审批通过 → Breakpoint 转回 Working → indicator 恢复滚动
- 审批拒绝 → Turn 终止 → indicator 编辑为 `✗ 已取消`

### 7. 账号级开关（不做全局开关）

在 `ChannelAccount.Settings`（JSON）新增 key：
```json
{"progressIndicator": {"enabled": true, "style": "verbose"}}
```

默认值（按 connector 类型）：
- Telegram / Feishu：`enabled=true, style=verbose`
- 其他所有连接器：`enabled=false`（UI 不显示开关，只读）

`style`：
- `verbose` — 显示 step / elapsed / toolName（如 "🔄 读取文件 (fs_read) · 第 3 步 · 8s"）
- `compact` — 只显示 "🔄 正在处理..."（手机用户减噪选项）

### 8. 文案本地化

indicator 文案用连接器内部双语常量（与 `FormatSessionStatusMessage` 同一套术语：`BreakpointState.PreModel` → "准备调用模型" 等），不走前端 i18n 后端。工具名 friendly display 优先复用 Iter 71 引入的 `TOOL_DISPLAY_NAMES`（如果已作为共享表存在），否则 fallback 原工具名。

本迭代**不强制**将 Iter 71 的 TOOL_DISPLAY_NAMES 抽 Contracts；若需要在 `ChannelProgressIndicator` 复用，就地定义一份同样的小表即可，后续可合并。

---

## 变更文件清单

| 文件 | 变更 |
|------|------|
| `src/KodaClaw.Contracts/Channels/IChannelConnector.cs` | 加 `SupportsEdit` / `SendWithReceiptAsync` / `EditAsync` 默认实现 |
| `src/KodaClaw.Contracts/Channels/ChannelSendReceipt.cs` | **新** record |
| `src/KodaClaw.ChannelHub/Turn/ChannelProgressIndicator.cs` | **新**组件 |
| `src/KodaClaw.ChannelHub/Turn/ChannelTurnOrchestrator.cs` | 选路 indicator/streamer；DoneEvent 分支 |
| `src/KodaClaw.ChannelHub/Delivery/ChannelDeliveryDispatchService.cs` | 加 `SendProgressInitialAsync` / `EditProgressAsync` |
| `src/KodaClaw.ChannelHub/Connectors/Telegram/TelegramConnector.cs` | `SupportsEdit=true` + `EditAsync` |
| `src/KodaClaw.ChannelHub/Connectors/Telegram/HttpTelegramApiClient.cs` | 加 `EditMessageTextAsync` |
| `src/KodaClaw.ChannelHub/Connectors/Feishu/FeishuConnector.cs` | `SupportsEdit=true` + `EditAsync` |
| `src/KodaClaw.ChannelHub/Connectors/Feishu/HttpFeishuApiClient.cs` | 加 `PatchMessageAsync` |
| `apps/kodaclaw-web/src/shell/desks/ChannelsDesk.tsx` | 进度指示开关 UI（Telegram/Feishu only） |
| `apps/kodaclaw-web/src/types/contracts.ts` | `ChannelAccountSettings` 加 `progressIndicator?` |
| `tests/KodaClaw.UnitTests/ChannelHub/ChannelProgressIndicatorTests.cs` | **新** L1 |
| `tests/KodaClaw.IntegrationTests/ChannelHub/TelegramProgressIndicatorIntegrationTests.cs` | **新** L2 |
| `tests/KodaClaw.IntegrationTests/ChannelHub/FeishuProgressIndicatorIntegrationTests.cs` | **新** L2 |
| `tests/KodaClaw.ContractTests/Channels/ChannelSendReceiptContractTests.cs` | **新** L3 |

---

## 验证命令

```bash
# L0
dotnet build KodaClaw.sln
npm run typecheck

# L1 / L2 / L3
dotnet test KodaClaw.sln -m:1 --filter "ChannelProgressIndicator|ChannelSendReceipt"

# L5 Dogfood（必须，高风险能力 — 完成后立即触发）
# 1. Telegram：发"列出 workspace 下所有 md 文件"→ 同一条进度消息随 tool 调用滚动更新 →
#    Done 后该条变 "✓ 完成 · N 步 / Ts"，final reply 另发一条
# 2. 飞书：同场景 → PATCH 成功，message_id 一致，行为一致
# 3. 钉钉或微信：同场景 → 不出现进度消息，最终回复正常
# 4. 关闭 Telegram indicator（Settings→Channels→进度指示 off）→ 回退到现有 Streamer 行为
# 5. 长 turn（>10 次状态切换）→ 观察节流生效，不刷屏
# 6. 模拟 edit 失败（拔网或 mock）→ degraded 后不再 edit，终态跳过，final reply 正常到达
# 7. 审批流：触发 bash_run 审批 → indicator 显示 ⏸ 等待审批 → 审批通过后恢复滚动 → 拒绝则 ✗ 已取消
```
