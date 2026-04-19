# Context Compression

## Overview

`ContextManager` 在每次模型调用前检测上下文 token 用量，超过阈值时自动触发压缩。压缩采用**三层架构**，确保长会话中关键信息不会丢失。

---

## 三层架构

```
┌──────────────────────────────────────────────────────┐
│  Layer 1 – Core-Memory Block                         │  ~1500 tokens，永不删除
│  <core-memory> system message                        │  每次压缩由 LLM 更新
│  当前任务 / 修改文件 / 关键决策 / 待办事项           │
├──────────────────────────────────────────────────────┤
│  Layer 2 – Summary Stack                             │  叠加式，永不删除
│  <context-summary> system messages                   │  每次压缩追加一条
│  [summary-1] → [summary-2] → [summary-N]            │
├──────────────────────────────────────────────────────┤
│  Layer 3 – Recent Messages                           │  按 token 预算动态选取
│  普通 user / assistant / tool 消息                  │  低价值消息优先删除
└──────────────────────────────────────────────────────┘
```

### 为什么要三层？

朴素的"截断旧消息"策略存在两个根本问题：

1. **摘要被覆盖**：每次压缩生成的摘要本身在下次压缩时可能被删掉，信息指数级衰减。
2. **重要上下文丢失**：早期的任务目标、文件修改记录、关键决策没有持久载体。

三层架构中，Layer 1 和 Layer 2 永不参与压缩删除，Layer 3 才是被压缩的对象。

> 设计参考：MemGPT（[arxiv 2310.08560](https://arxiv.org/abs/2310.08560)）将 LLM 上下文类比为操作系统虚拟内存，Core Memory = RAM（永远可见），Recall Memory = 磁盘（可检索）。

---

## 触发与压缩流程

```
每次模型调用前
       │
       ▼
  ContextManager.Analyze()
  CJK-aware token 估算
       │
  totalTokens > MaxTokens?
       │ Yes
       ▼
  分离 pinned（Layer 1+2）vs regular（Layer 3）消息
       │
  按重要性打分，token 预算内选取保留消息
       │
  IContextSummarizer.SummarizeAsync()
  ┌─────────────────────────────────────┐
  │ LlmContextSummarizer（默认）        │
  │   - 生成语义摘要                    │
  │   - 更新 core-memory block          │
  │   - 失败自动降级到 StaticSummarizer │
  └─────────────────────────────────────┘
       │
  重建消息列表：
  [core-memory] + [summary stack] + [new summary] + [retained]
       │
       ▼
  继续调用模型
```

---

## Token 估算

旧版使用 `chars / 4` 均一估算，对中文严重低估（实际偏差约 4×）。

新版区分字符类型：

```csharp
// CJK 字符（汉字/假名/韩文）≈ 1.5 tokens/char
// 其他字符（英文/符号）≈ 0.25 tokens/char
```

> 数据来源：[arxiv 2305.15425](https://arxiv.org/abs/2305.15425)（NeurIPS 2023）实测中文为英文的 1.76×；本实现取保守值 1.5×。

---

## 消息重要性打分

Layer 3 消息在 token 超出预算时按分数从低到高删除：

| 分量 | 范围 | 说明 |
|------|------|------|
| Recency | 0–40 | 越新得分越高，线性插值 |
| Role | 0–30 | User 消息 30，Assistant 15 |
| ToolType | -20–+20 | `fs_write`/`workspace_*` = +20；纯 `bash_logs` 轮询 = -20 |

**最终得分 = Recency + Role + ToolType**，低分消息优先被移除。

### tool_use / tool_result 原子对保护

压缩前先扫描全部消息，建立 `toolUseId → message index` 和 `toolResultId → message index` 双向映射。移除某条消息时：

- 若该消息含 `tool_use`，自动同时移除含对应 `tool_result` 的消息（反之亦然）；
- 若配对消息位于 `minRecentCount` 保护区，则**整对不移除**，保证 API 合法性。

这避免了 `SanitizeOrphanToolResults` 不得不将孤立 tool_result 改写为文本的情况，减少信息损失。

### 系统 prompt token 计入

`Analyze(messages, systemPromptTokens)` 新增第二参数（默认 0）。`Agent.cs` 在每次压缩判断前调用 `ContextManager.EstimateSystemPromptTokens(_systemPrompt)` 得到估算值并传入，确保 system prompt（通常 3 000–10 000 tokens）被计入触发阈值，避免"消息看起来没超限但加上 system prompt 已经超窗口"的情况。

> 设计参考：[LLMLingua-2](https://arxiv.org/abs/2403.12968)（Microsoft Research）的 Budget Controller 按重要性分配压缩比例；通用权重公式 `Relevance×0.4 + Importance×0.3 + Recency×0.2 + Frequency×0.1`。

---

## IContextSummarizer 接口

```csharp
public interface IContextSummarizer
{
    Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<Message> removedMessages,
        ContextManagerOptions options,
        CancellationToken cancellationToken = default);
}

public record SummaryResult(
    string Summary,            // 注入为 <context-summary> system message
    string? CoreMemoryUpdate   // null = 保留旧 core-memory 不变
);
```

### 内置实现

| 实现 | 行为 |
|------|------|
| `LlmContextSummarizer` | 调用 `IModelProvider.CompleteAsync`，一次请求同时生成 summary 和 core-memory。输入限制 12,000 字符，连续 `bash_logs` 轮询自动去重。失败自动降级。 |
| `StaticContextSummarizer` | 纯文本统计（消息数、工具调用数、首尾用户消息摘要）。不调用模型，零额外成本。 |

### 自定义 Summarizer

```csharp
public class MyCustomSummarizer : IContextSummarizer
{
    public async Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<Message> removedMessages,
        ContextManagerOptions options,
        CancellationToken cancellationToken = default)
    {
        // 自定义摘要逻辑
        return new SummaryResult("...", "## Current Task\n...");
    }
}

// 注入：
var summarizer = new MyCustomSummarizer();
var contextManager = new ContextManager(store, agentId, options, summarizer);
```

---

## 配置参考

```csharp
var config = new AgentConfig
{
    Context = new ContextManagerOptions
    {
        // 触发压缩的 token 上限
        // KodaClaw 默认：DefaultContextWindowSize × 0.75 = 96,000
        MaxTokens = 96_000,

        // 压缩后 Layer 3 的 token 目标
        // KodaClaw 默认：DefaultContextWindowSize × 0.40 = 51,200
        CompressToTokens = 51_200,

        // 压缩用模型，null = 使用 Agent 主模型
        // 建议设置为轻量模型（如 claude-haiku-4-5-20251001）以降低成本
        CompressionModel = "claude-haiku-4-5-20251001",

        // 自定义压缩 prompt，空字符串 = 使用内置英文 prompt（见下节）
        CompressionPrompt = "",

        // 是否启用 core-memory block（MemGPT 风格任务状态持久化）
        EnableCoreMemory = true,

        // 最少保留的最近消息数（防止 summary stack 过大时丢失所有近期上下文）
        MinRecentMessages = 6,

        // summary stack 最大深度；超限时触发递归合并
        MaxSummaryDepth = 5,
    }
};
```

---

## CompressionPrompt 接入指南

`CompressionPrompt` 是面向**接入方产品**的核心扩展点，允许不同业务场景的压缩行为在不修改 SDK 的前提下独立定制。

### 默认行为（空字符串）

`LlmContextSummarizer` 内置英文 prompt，适合通用对话 / 代码助手场景：
- `<summary>`：任务目标、已完成步骤、关键文件路径、重要决策、剩余工作
- `<core-memory>`：Current Task / Modified Files / Key Decisions / Next Steps

### 何时需要自定义

| 场景 | 默认 prompt 的问题 | 应侧重的信息 |
|------|------------------|-------------|
| 渠道消息（IM Bot） | "Modified Files"无意义，丢失消息收发记录 | 发送方账号、消息内容、发送结果、用户偏好 |
| 自动化任务 | 对话式摘要格式不适合结构化日志 | 任务名、执行步骤状态（✓/✗）、产出物、错误处理 |
| 客服 / 工单系统 | 通用摘要丢失工单 ID 和 SLA 信息 | 工单号、问题描述、当前状态、承诺时间 |
| 数据分析 Agent | 文件路径不如数据集名称和指标重要 | 数据集、查询逻辑、关键发现、图表路径 |

### 接入示例

**最小接入（使用默认 prompt）**：
```csharp
Context = new ContextManagerOptions
{
    MaxTokens = 96_000,
    CompressToTokens = 51_200,
    // CompressionPrompt 留空 → 使用内置 prompt
}
```

**渠道 Bot 场景**：
```csharp
Context = new ContextManagerOptions
{
    MaxTokens = 96_000,
    CompressToTokens = 51_200,
    CompressionPrompt = """
        You are compressing a channel conversation history.
        Produce your response in EXACTLY this XML format:

        <summary>
        Concise summary (under 400 words).
        Include: sender accounts, key messages received, replies sent, delivery outcomes.
        Omit: repetitive polling, transient error retries.
        </summary>

        <core-memory>
        ## Active Channels
        [Channel accounts in use]

        ## Recent Thread
        [Last 2-3 turns with sender attribution]

        ## Delivery Status
        [Pending / failed deliveries, if any]
        </core-memory>
        """,
}
```

**自动化任务场景**：
```csharp
Context = new ContextManagerOptions
{
    MaxTokens = 96_000,
    CompressToTokens = 51_200,
    CompressionPrompt = """
        You are compressing an automation task history.
        Produce your response in EXACTLY this XML format:

        <summary>
        Summary (under 400 words).
        Include: task name, trigger time, steps with ✓/✗ status, outputs, error handling.
        </summary>

        <core-memory>
        ## Task
        [Task name and schedule]

        ## Execution Log
        [Steps: ✓ success / ✗ failed / ⚠ partial]

        ## Outputs
        [Files written, messages sent, API calls made]

        ## Next Steps
        [Remaining work in this run]
        </core-memory>
        """,
}
```

### Prompt 编写规则

1. **格式固定**：必须要求模型输出 `<summary>` + `<core-memory>` 两段 XML，其余内容不输出。`LlmContextSummarizer.ParseResponse()` 依赖这两个 tag 提取结果；如果缺失 `<summary>`，整个响应会被当作 summary 文本，`core-memory` 不更新。
2. **字数预算**：`<summary>` 建议 300–500 词，`<core-memory>` 建议 200–400 词，合计不超过 700 词。`LlmContextSummarizer` 设定 `MaxTokens = 1200`，超出会截断。
3. **语言一致性**：Prompt 用英文编写（模型对英文指令响应更稳定），summary 内容可以是中英混合。
4. **`<core-memory>` 结构**：每个 `##` section 都会在下次压缩时被完整覆盖，建议保持扁平列表，避免深层嵌套。
5. **`EnableCoreMemory = false` 时**：`<core-memory>` 段即使 LLM 生成了也会被忽略，可在 prompt 中省略该段以节省 token。

### KodaClaw 中的覆盖路径

KodaClaw 的三个 session service 均通过 `*SessionOptions.CompressionPrompt` 向下透传：

```
MainSessionOptions.CompressionPrompt           = ""（空，使用内置默认）
ChannelSessionOptions.DmCompressionPrompt      = ChannelCompressionPrompts.Dm
ChannelSessionOptions.GroupCompressionPrompt   = ChannelCompressionPrompts.Group
AutomationSessionOptions.CompressionPrompt     = AutomationCompressionPrompts.Default
```

Channel session 运行时按 `ChannelThreadType` 选择 prompt：
- `DirectMessage` → `DmCompressionPrompt`（Owner 信任级别，关注个人任务 + workspace 变更）
- `Group` → `GroupCompressionPrompt`（多参与者，关注群组上下文 + @Koda 的对话）

Prompt 常量集中在 `KodaClaw.Runtime/Sessions/CompressionPrompts.cs`，后续产品按需覆盖对应 Options 属性即可。

KodaClaw 阈值计算路径（不变）：

```
*SessionOptions.DefaultContextWindowSize = 128,000
ContextCompressionTriggerRatio = 0.75  →  MaxTokens = 96,000
ContextCompressionTargetRatio  = 0.40  →  CompressToTokens = 51,200
```

---

## 压缩后的消息结构示例

第一次压缩后：

```
[system] <core-memory>
  ## Current Task
  实现钉钉 ActionCard 消息发送
  ## Modified Files
  - src/.../DingTalkConnector.cs
  ## Key Decisions
  - Stream 模式优先于 Webhook
</core-memory>

[system] <context-summary timestamp="..." window="window-1">
  用户要求接入钉钉机器人。已完成 Stream 模式建连和基础文本收发...
</context-summary>

[user] 现在帮我加群聊支持
[assistant] 好的，我来看一下当前的实现...
...（最近消息）
```

第二次压缩后（summary 叠加）：

```
[system] <core-memory>（更新后）
[system] <context-summary window="window-1">（保留，永不删除）
[system] <context-summary window="window-2">（新增）
...（最近消息）
```

---

## 重大修复与优化（2026-04）

本节记录 2026-04 期间针对压缩链路的三处 bug 修复和两项优化。每项均给出**问题现象 → 根因 → 修复方案 → 为什么有效**四段式，便于后续回溯。

### Bug #1：Provider 丢弃 core-memory / summary 系统消息

**问题现象**。生产日志反复出现 `InvalidOperationException: model_empty_response`（`Agent.Step.cs:212`）。追踪数据流发现：`ContextManager.CompressAsync` 生成的 `<core-memory>` 和 `<context-summary>` 两个系统消息在进入 Provider 之前就被过滤掉了，模型永远看不到三层架构的 Layer 1 和 Layer 2。每次压缩后下一轮对话等价于只保留 Layer 3 + 主 system prompt——上下文重新溢出，模型返回空响应。

**根因**。三个 Provider 都做了"只保留主 system prompt，其余 system 消息忽略"的简化：

- `AnthropicProvider.BuildMessageParameters` 把 `request.SystemPrompt` 直接赋给 `MessageCreateParams.System`，循环里对 `MessageRole.System` 的消息走 `continue`。
- `OpenAIProvider.BuildChatMessages` 相同逻辑，只向 `messages` 列表推入一个 `SystemChatMessage`。
- `OpenAIResponsesProvider.BuildRequestBody` 用 Responses API 的 `instructions` 字段，单字符串语义，无法自然承载多段。

这是在 core-memory 架构引入之前就存在的旧实现，架构升级时漏改了这一层。

**修复方案**：

- **AnthropicProvider**：通过 monodis 验证 SDK 12.9.0 的 `MessageCreateParamsSystem` 支持 `op_Implicit(List<TextBlockParam>)`。当检测到有 `MessageRole.System` 消息时，把主 prompt + 每条 memory 块分别包装成 `TextBlockParam` 压成 `List`。
- **OpenAIProvider**：在主 system 消息之后，继续遍历 `MessageRole.System` 消息，每条文本追加一个 `SystemChatMessage`。OpenAI SDK 原生支持多条 system chat message。
- **OpenAIResponsesProvider**：Responses API 的 `instructions` 只允许单字符串，用 `\n\n` 把主 prompt 和所有 memory 块拼接成一段。

**为什么有效**。三层压缩架构的核心假设是"pinned system messages 永远可见"。修复后这条链路真正贯穿到 API 边界。回归测试 `CompleteAsync_WithSystemRoleMemoryMessages_PreservesThemAsSystemMessages` 断言 "主 prompt + core-memory + summary" 三条 system 消息都到达请求体。

---

### Bug #2：Force-compress 路径在估算偏差时失效

**问题现象**。Bug #1 修好后偶发仍然抛 `model_empty_response`。复现路径：GLM-5-Turbo 返回 `model_context_window_exceeded` 被标记为 `ContextOverflow`，`Agent.Step.cs:175` 触发 `CompressAsync(force=true)`——但 `CompressionResult.RemovedMessages.Count == 0`，压缩"空跑"，重试请求再次溢出。

**根因**。CJK 估算存在系统性低估，本地估出的 `total` 长期小于 `CompressToTokens`，`SelectMessagesByBudget` 的预算判断认为"无需删除"。`force=true` 只是跳过了 `ShouldCompress` 阈值检查，并没有改变预算选择算法；本地估算跟真实 token 数严重不一致时，force 分支等价于没跑。

**修复方案**。`CompressAsync` 中在 `SelectMessagesByBudget` 返回后补一个 **force-mode 硬截断兜底**：

```csharp
if (force && removedMessages.Count == 0 && regularMessages.Count > _options.MinRecentMessages)
{
    var keep = _options.MinRecentMessages;
    var cutoff = regularMessages.Count - keep;
    retainedRegular = regularMessages.Skip(cutoff).ToList();
    removedMessages = regularMessages.Take(cutoff).ToList();
    _logger?.LogWarning("Force-compress hard-truncate engaged: estimate undercounted tokens; ...");
}
```

**为什么有效**。这条路径只在"模型已经明确说装不下"时触发，此时应当信任模型而不是本地估算。硬截断是确定性的尾部裁切，保留最后 `MinRecentMessages` 条，永远能释放空间。随后重试的请求体一定比原来小，从根上打破了"估算不准→永远不压缩→模型永远溢出"的死循环。

---

### Bug #3：空 removedMessages 污染 core-memory

**问题现象**。即使不触发 force 路径，某些会话的 `<core-memory>` 块也会被莫名其妙地覆盖为 `[None]` 占位符；`summary stack` 多出若干条 `No conversation history was provided` 的假摘要。

**根因**。当 `SelectMessagesByBudget` 因为"保护窗口全覆盖 + 低估 total"等原因返回 `removedMessages=[]` 时，旧代码依旧去调用 `IContextSummarizer.SummarizeAsync(removedMessages=[])`。LLM 收到空输入只能产出 "No conversation history was provided" 这类退化响应。然后：

1. `ParseResponse` 把退化响应误当成有效 summary，压入 stack；
2. `CoreMemoryUpdate` 从这个退化响应里解析出 `## Current Task\n[None]` 等等，**直接覆盖**健康的 core-memory 块。

两次压缩后原本 core-memory 保存的"当前任务 / 修改文件 / 关键决策"全部变成 `[None]`，整个三层架构瓦解。

**修复方案**：

1. **LlmContextSummarizer 防御短路**（属地防御）：
   ```csharp
   if (removedMessages.Count == 0)
       return new SummaryResult(string.Empty, null);
   ```
2. **ContextManager 调用点短路**（主防御）：
   ```csharp
   var mergeOverdue = summaryStack.Count >= _options.MaxSummaryDepth;
   if (removedMessages.Count == 0 && !mergeOverdue)
       return null;  // 没有可压缩的新材料且不到合并门槛，no-op
   ```
3. 把 `summaryResult` / `newSummaryMsg` 改成 nullable，重建路径允许 "merge 了但没有新 summary" 的分支：
   ```csharp
   var resultSummaryMsg = newSummaryMsg ?? summaryStack[^1];
   ```

**为什么有效**。Bug 的本质是"向 LLM 提交空材料 → 产生退化响应 → 退化响应反向写回 pinned 层"。关键是把"没有新材料可压缩"从一个静默退化点变成显式 no-op。同时保留 `mergeOverdue` 通道：哪怕当前轮没有 removed 消息，只要 summary 栈积攒到 `MaxSummaryDepth`，merge 仍会执行，避免无界增长。`ContextManagerTests.Compress_WhenSummaryStackMerges_CoreMemoryIsUpdated` 就测这条 merge-without-new-material 的路径不被回归破坏。

---

### 优化 #1：服务端 usage 回填 token 估算（Phase 2）

**动机**。Bug #2 虽然兜住了最坏情形，但根源——本地 CJK-aware 估算和真实 tokenizer 有 1.5× ~ 2× 偏差——没解决。理想情况是让压缩触发点（`ShouldCompress`）在"真实 token 溢出之前"就正常生效，而不是等模型报错再硬截断。

**设计**。在 `ContextManager` 上加一个会话级校准因子 `_calibrationFactor`（默认 1.0），每次流式响应结束时取服务端回传的 `usage.InputTokens` 和我们发请求前算出的**原始估算** raw，按 EMA 平滑更新：

```csharp
private const double CalibrationSmoothing = 0.3;     // 新样本权重
private const double MinCalibrationFactor = 0.5;     // 噪声下限
private const double MaxCalibrationFactor = 5.0;     // 噪声上限

public void RecordServerUsage(int actualInputTokens, int rawEstimate)
{
    if (actualInputTokens <= 0 || rawEstimate <= 0) return;
    var sample = (double)actualInputTokens / rawEstimate;
    if (sample < MinCalibrationFactor || sample > MaxCalibrationFactor) return;
    _calibrationFactor = _calibrationFactor * 0.7 + sample * 0.3;
}
```

应用点有三处：

| 应用点 | 换算 |
|---|---|
| `Analyze().TotalTokens` | `raw × factor`（对外呈现为真实 token） |
| `Analyze().ShouldCompress` | 用 `raw × factor` 跟 `MaxTokens × 0.9` 比（阈值侧用真实单位） |
| `CompressAsync` 的 `regularBudget` | `(CompressToTokens − 1200) / factor − pinnedRaw − sysRaw`（把真实预算换成 raw 单位，供 `SelectMessagesByBudget` 内部比较） |

`Agent.ModelStreaming` 在 `usage != null` 分支里用 `EstimateMessagesTokensRaw(request.Messages, EstimateSystemPromptTokens(request.SystemPrompt))` 拿到本次请求的 raw 估算，然后 `RecordServerUsage(usage.InputTokens, rawEstimate)`。

**为什么有效**：

1. **对抗估算偏差**。如果 raw 系统性低估 2×，三轮样本后 factor ≈ 1.9，下一次 `Analyze` 汇报的 `TotalTokens` 就回到真实量级。`ShouldCompress` 在真实 token 到达阈值时就能触发，而不用等到 force 路径。
2. **EMA + 噪声过滤稳定性**。0.3 权重意味着要 5~6 个样本 factor 才会走完半程，单次异常样本拉不动整体；`[0.5, 5.0]` 区间裁掉首轮 `input_tokens=0` / 极端短请求等噪声来源。
3. **配置语义对齐用户直觉**。`MaxTokens` / `CompressToTokens` 配置值从"raw 估算单位"修正为"真实 token 单位"——用户填 `50_000` 就是 50k 真实 token，不用关心本地估算器的偏差。

测试覆盖：`RecordServerUsage_UpdatesFactorTowardActualRatio`（单样本 EMA）、`RecordServerUsage_ConvergesWithRepeatedSamples`（多样本收敛）、`RecordServerUsage_IgnoresNoiseAndBadInputs`（边界裁剪）、`Analyze_AppliesCalibrationFactorToTotalTokens`（对外值）、`Analyze_ShouldCompress_FiresWhenCalibratedTotalExceedsThreshold`（阈值触发）。

**Usage 缺失 / 异常时的降级行为**。三道防线串联确保「没 usage」不比优化前更糟：

1. 外层 `if (usage != null)`（`Agent.ModelStreaming`）——Provider 不回 usage、流中断、某些 compat-API 不发 `message_delta` 时整段校准跳过。
2. `RecordServerUsage` 入口：`actualInputTokens <= 0 || rawEstimate <= 0` 直接 return——`usage != null` 但 `InputTokens == 0` 的场景（Anthropic fallback JSON 解析失败等）全部短路。
3. `sample < 0.5 || sample > 5.0` 丢弃——极端异常值（比如某 compat-API 回传累计 total 而不是本次 input）大多被裁掉。

没有任何有效样本时 `_calibrationFactor` 保持 1.0，`Analyze` 的 `calibratedTotal = raw × 1.0` 退化为原始 CJK 估算，`CompressAsync` 的 `compressTokensRaw = CompressToTokens / 1.0` / `summaryHeadroomRaw = 1200 / 1.0` 也跟旧代码字节一致。真正的溢出由 Bug #2 的 force 硬截断兜底。结论：**usage 数据缺失 / 全零 / 不可靠的情形下，整个链路退化到 "修完 Bug #2 之后 + 无校准" 的行为，不引入新的失败模式。**

---

### 优化 #2：Tool-result 级 micro-compaction（Phase 2）

**动机**。在 Layer 3 的 message 粒度压缩之前，许多 tool_result 其实已经被后续同等调用"覆盖"了——例如：

- `bash_logs` 轮询同一个 `processId` 十几次，前 N 次的输出都是最终输出的前缀；
- `fs_read` 读同一个文件同一段范围多次（agent 路径选择错误常见），后一次的内容跟前一次完全等价。

这些冗余 payload 正是 Anthropic 所说的 **micro-compaction** 层的典型目标：不改变消息结构，仅剥离早已失效的 payload。

**设计**。在 `CompressAsync` 的 `SelectMessagesByBudget` 之前跑 `MicroCompactSupersededToolResults(regularMessages)`：

```csharp
private static readonly HashSet<string> MicroCompactableTools =
    new(StringComparer.OrdinalIgnoreCase) { "fs_read", "fs_grep", "fs_glob", "fs_list", "bash_logs" };

// key = tool_name + canonical-JSON(input)
// 顺序遍历，相同 key 再次出现 → 前一个 tool_use_id 记入 supersededBy
// 第二趟：把 supersededBy 里的 tool_use_id 对应的 tool_result.Content 换成存根
rewritten[i] = tr with {
    Content = $"[tool_result superseded by {newerId}: a later call with identical arguments produced a fresher result]"
};
```

关键设计约束：

1. **仅幂等只读工具**。`bash_run`、`fs_write`、`fs_edit` 等有副作用或 output 因时间/副作用变化的工具严禁纳入——白名单硬编码，不让配置意外打开。
2. **保留 tool_use 块本身**。只替换 `tool_result.Content`，`ToolUseContent` 和 `ToolResultContent.ToolUseId` 原样保留，`SanitizeOrphanToolResults` 和 `SelectMessagesByBudget` 的成对原子删除逻辑完全不受影响。
3. **canonical key 用完整 JSON**。同路径不同 `startLine/endLine` 算不同 key，因此读不同段落不会相互覆盖；同路径同范围才会 dedup。这是"信息等价"的最保守等价关系。
4. **存根保留指向**。老的 `ToolUseId` 不丢，存根文本里携带 `newerId`，方便 agent 追踪到最新结果，也方便调试。

**为什么有效**：

1. **命中高频场景**。agent 轮询 bash_logs 等待编译这种模式，单会话轻松 10 ~ 20 次重复，每次结果可能几十 KB。micro-compact 之后 raw total 直接骤降，往往不需要真的删除任何消息 `SelectMessagesByBudget` 就能满足预算。
2. **零信息损失（语义意义上）**。被替换的 payload 都有等价或更新的后续版本，agent 如果想 reference 旧数据，它想要的信息早已在新的 tool_result 里了。存根的文本还给出新 id 作为指路牌。
3. **保持架构正交**。micro-compaction 是 Layer 3 之内的预处理，跟 Layer 1/2（core-memory / summary stack）的生成机制完全解耦；failure 模式独立——即便这层抛异常（不会，它是纯函数），后续的 SelectMessagesByBudget / Summarizer 链路不受影响。

测试覆盖：无重复不改动 / fs_read 同路径去重 / fs_read 不同 range 并存 / bash_logs 连续轮询折叠 / bash_run 即使参数相同也不 dedup（白名单测试）。

---

## 已知局限

- **工具结果语义丢失**：`fs_read` 读取的文件内容不会保存在摘要中（仅记录文件路径）。如需跨压缩保留文件内容，使用 `IFilePool` + `RecoveredFile` 机制。
- **单模型上下文**：压缩调用使用同一 `IModelProvider`，不支持为压缩单独路由到不同 endpoint。如需此能力，可实现自定义 `IContextSummarizer`。
- **Calibration 冷启动**：`_calibrationFactor` 默认 1.0，会话前几轮仍以原始 CJK 估算决策。会话开始后若立即遇到大段 CJK 输入，仍可能触发 force 硬截断路径一次，用真实 usage 校准后后续轮次即稳定。

---

## 参考文献

| 文献 | 贡献 |
|------|------|
| [MemGPT: Towards LLMs as Operating Systems](https://arxiv.org/abs/2310.08560) | 三层虚拟内存架构，Core Memory 永不驱逐 |
| [LLMLingua-2: Data Distillation for Efficient and Faithful Task-Agnostic Prompt Compression](https://arxiv.org/abs/2403.12968) | 基于重要性的 token 预算分配，Budget Controller |
| [Language Model Tokenizers Introduce Unfairness Between Languages](https://arxiv.org/abs/2305.15425) | CJK 语言 token 倍率实测数据（NeurIPS 2023） |
| [Effective Context Engineering for AI Agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) | Claude Code 三层压缩策略（micro/auto/manual） |
| [Letta (formerly MemGPT)](https://github.com/letta-ai/letta) | Memory Block 实现参考，autonomous memory editing |
