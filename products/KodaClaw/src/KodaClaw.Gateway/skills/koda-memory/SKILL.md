---
name: koda-memory
description: 记忆管理指南——三层记忆架构（热/温/冷）、MEMORY.md 分层索引、topics 主题索引、记忆元数据、时序数据分离、session 清理、Nightly Auto Dream 整合。当 Agent 需要存储、检索、整合或管理用户上下文记忆时触发。
license: MIT
metadata:
  kind: builtin-core
  version: "3.2"
  compatibility: KodaClaw 1.x
  allowed-tools: workspace_memory_append workspace_protocol_update workspace_read fs_grep fs_glob
  tags: "memory, workspace, context, persistence, search, topics, metadata"
---

# KodaClaw Memory — 记忆管理指南 v3.2

KodaClaw 采用三层记忆架构管理用户上下文：热记忆（每次加载）、温记忆（按需搜索）、冷记忆（归档检索）。日常对话通过 `workspace_memory_append` 写入，Nightly Agent 通过 `workspace_protocol_update` 执行整合。核心原则：**只记未来需要的信息，不记可实时获取的数据，不记时序快照**。

## Execution Procedure

```pseudocode
# 入口 1：日常写入 — 对话中触发
remember(input):
  read("references/time-series-separation.md")  # 判断是否时序数据
  read("references/memory-metadata.md")          # 确认元数据格式
  if not need_memory(input) → return  // 写入判断五问法
  if is_time_series(input) → write_data_channel(input) → return
  dedup = search_existing(input)
  if dedup found → update(dedup) else → append(input, priority, tags)

# 入口 2：Nightly 整合 — HEARTBEAT 自动触发
consolidate():
  read("references/memory-architecture.md")      # 三层架构和降级规则
  read("references/memory-index.md")             # Critical/Active/Index 分层标准
  read("references/memory-metadata.md")          # 元数据格式要求
  read("references/topics-guide.md")             # topics 维护规则
  read("references/nightly-consolidation.md")    # 六阶段完整流程
  collect daily_logs + sessions + MEMORY.md
  merge, dedup, tag → rewrite MEMORY.md (Critical ≤20, Active ≤50)
  update_topics(≤15) → cleanup archive → freshness_review
  validate_references → git_commit()
```

## TOC

- [记忆架构](#记忆架构)
- [MEMORY.md 索引](#memorymd-索引)
- [记忆元数据](#记忆元数据)
- [时序数据分离](#时序数据分离)
- [Topics 主题索引](#topics-主题索引)
- [写入记忆](#写入记忆)
- [读取与搜索](#读取与搜索)
- [Session 管理](#session-管理)
- [Nightly 整合](#nightly-整合)
- [何时不写记忆](#何时不写记忆)

---

## 记忆架构

三层记忆（热/温/冷）的生命周期与降级管理。详细规范见 → references/memory-architecture.md

| 层级 | 存储位置 | 说明 |
|------|---------|------|
| 热（Hot） | `workspace/MEMORY.md` | 每次启动载入，Critical ≤20 + Active ≤50 |
| 温（Warm） | `workspace/memory/dormant/` | 不自动加载，可通过搜索访问 |
| 冷（Cold） | `workspace/memory/archive/` | 长期归档，仅手动检索 |

降级由 Nightly Agent 基于语义判断执行，非时间公式。

## MEMORY.md 索引

分层索引结构（Critical / Active / Index）与来源溯源规则。详细规范见 → references/memory-index.md

核心规则：Critical ≤20 条永久级信息；Active 按主题分组每组 ≤5 条；Index 只存路径引用。每条记忆附来源链接（`→ daily/`、`→ sessions/`、`→ topics/`）。

## 记忆元数据

每条记忆的元数据规范（tags/updated/source/valid_until）与过期机制。详细规范见 → references/memory-metadata.md

```
- REST API 分页 bug：同时传 page 和 offset 时 offset 被忽略
  tags: [api, bug, pagination]  updated: 2026-04-17  source: → daily/2026-04-17
```

标签用小写英文，同一概念统一标签名。带 `valid_until` 的条目 Nightly 优先过期检查。

## 时序数据分离

重复性、周期性时序数据不写入 memory，写入独立数据通道。详细规范见 → references/time-series-separation.md

判断标准：周期性监控快照、无状态变化的例行记录、仪表盘式数据 → 写 `workspace/data/snapshots/`。daily memory 只记状态变化事件（故障、恢复、配置变更、结论性判断）。

## Topics 主题索引

`workspace/memory/topics/` 是唯一的 topics 存储位置，≤15 个。详细规范见 → references/topics-guide.md

```
workspace_read(target="topics")                                # 列出所有
workspace_read(target="topics", path="project-architecture")   # 读取指定
```

⚠️ `workspace/topics/` 是遗留目录，新内容一律写 `workspace/memory/topics/`。

## 写入记忆

### workspace_memory_append — 日常唯一写入路径

```
workspace_memory_append(
  content="REST API 分页 bug：同时传 page 和 offset 时 offset 被忽略。解决：拆成两个独立查询接口",
  priority="standard", date="2026-04-17"
)
```

### 写入判断五问法

1. **未来需要？** — 不需要 → 不写
2. **可实时获取？** — 能从代码/文档获取 → 不写
3. **时序/监控？** — 是 → 写 data 目录，不写 memory
4. **重复信息？** — 是 → 不写或更新已有条目
5. **有明确标签？** — 有 → 附上 tags

### 优先级选择

| 优先级 | 适用场景 |
|--------|---------|
| `permanent` | 核心身份/价值观 — 永不降级 |
| `lasting` | 重要决策、长期项目 — 仅明显过时才降级 |
| `standard`（默认） | 技术教训、bug 记录 — 30+ 天无关联可降级 |
| `ephemeral` | 临时信息 — 7 天无价值即可降级 |

### workspace_protocol_update — Nightly 专用

⚠️ 仅 Nightly Consolidation Agent 整合时使用。详细用法见 → references/nightly-consolidation.md

## 读取与搜索

```
workspace_read(target="memory")           # MEMORY.md
workspace_read(target="daily_memory")     # 今日日志
workspace_read(target="topics")           # 所有 topic
fs_grep(pattern="关键词", path="workspace/memory/")           # 内容搜索
fs_grep(pattern="tags:.*frontend", path="workspace/MEMORY.md") # 标签搜索
fs_glob(pattern="workspace/memory/dormant/*.md")              # 温记忆
history_search(query="搜索词", limit=5)                       # 对话历史
```

按需加载：启动只加载 MEMORY.md；需主题详情读 topics；需历史细节 `fs_grep` 搜索 dormant/archive。搜索优先级：Critical → Active → topics/ → dormant/ → archive/。

## Session 管理

Session 轮转时自动生成摘要存入 `workspace/memory/sessions/`（≥5 条用户消息的对话型 session）。不包含敏感信息，用户说"不用记"的不纳入。

清理规则（Nightly Stage 4）：空壳 session（<500B）直接归档；已整合 session 7 天后归档。详见 → references/nightly-consolidation.md

## Nightly 整合

每晚 HEARTBEAT 自动触发的六阶段整合。详见 → references/nightly-consolidation.md

1. **采集** — 今日日志 + 未处理 session + MEMORY.md
2. **整合** — 合并去重、解决矛盾、附来源标签，重写 MEMORY.md
3. **Topics 维护** — 创建/更新主题文件，迁移遗留目录
4. **清理** — 归档 daily 源文件，清理空壳 session
5. **时效审查** — 检查 valid_until 过期，语义判断降级
6. **索引验证** — 检查引用链，归档 dormant >90 天

完成后自动 git commit。

## 何时不写记忆

**不写**：临时中间状态、可实时获取的信息、不影响未来的闲聊、时序监控快照、已存在的重复信息。

**应写**：重要决策、关键信息（职位变化/项目背景/偏好）、未来需引用的事件、待跟进计划、教训和 bug 记录。

---

| 文件 | 用途 | 变化频率 |
|------|------|---------|
| `USER.md` | 用户画像和稳定偏好 | 低（月级） |
| `MEMORY.md` | 事件日志和知识索引 | 高（周级） |
| `topics/` | 主题深度文档 | 中（按需更新） |
