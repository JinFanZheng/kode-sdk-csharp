---
name: koda-memory
description: 记忆管理指南——三层记忆架构（热/温/冷）、MEMORY.md 分层索引、topics 主题索引、记忆元数据、时序数据分离、session 清理、Nightly Auto Dream 整合
license: built-in
compatibility: KodaClaw 1.x
allowed-tools: workspace_memory_append workspace_protocol_update workspace_read fs_grep fs_glob
metadata:
  kind: builtin-core
  version: "3.2"
  tags: "memory, workspace, context, persistence, search, topics, metadata"
---

# KodaClaw Memory — 记忆管理指南 v3.2

## 三层记忆架构

KodaClaw 采用热-温-冷三层记忆架构，由 Nightly Agent 语义判断管理记忆生命周期：

| 层级 | 存储位置 | 说明 |
|------|---------|------|
| 热（Hot） | `workspace/MEMORY.md` | 活跃记忆，每次 session 启动载入 system prompt |
| 温（Warm） | `workspace/memory/dormant/` | 一段时间未访问的记忆，不再自动加载但可搜索 |
| 冷（Cold） | `workspace/memory/archive/` | 长期归档，仅通过手动检索访问 |

记忆优先级（供 Nightly Agent 参考，非硬编码阈值）：

- **permanent**：核心身份/价值观 — 永不降级
- **lasting**：重要决策/长期项目 — 仅当明显过时或被推翻时降级
- **standard**（默认）：一般事件记录 — 30+ 天无关联可降级
- **ephemeral**：临时记录 — 7 天无价值即可降级

降级由 Nightly Consolidation Agent 基于语义判断执行，而非时间阈值公式。

---

## MEMORY.md 分层索引

`MEMORY.md` 是长期记忆的索引文件，采用三层结构确保关键信息不被淹没：

```
# Long-Term Memory

## 🔴 Critical（每次必加载，≤ 20 条）
### 用户核心偏好（permanent）
- [偏好内容]

### 当前活跃目标（带 valid_until）
- [目标内容]

## 🟡 Active（按主题分组，每组 ≤ 5 条，总计 ≤ 50 条）
### 工程/技术
- [技术教训]

### 项目/工作
- [项目信息]

### 生活/职业
- [生活信息]

## 📚 Index（仅引用路径，不展开内容）
### Topics 详见 memory/topics/
### Dormant 详见 memory/dormant/
### Archive 详见 memory/archive/
```

### 分层规则

- **Critical 层**：用户核心偏好（permanent）、当前进行中的长期目标、未来 session 极可能需要的信息。严格控制 ≤ 20 条，超出时合并同类项或降级
- **Active 层**：按主题分组，每组 ≤ 5 条。主题包括但不限于：工程/技术、项目/工作、生活/职业、Agent协作。超出时合并或降级到 dormant
- **Index 层**：只存文件路径和一句话描述，不展开内容。需要时通过 `workspace_read` 或 `fs_grep` 按需读取

### 来源溯源

每条记忆应附来源链接，便于溯源：
- `→ daily/2026-04-17`（当日记忆日志）
- `→ sessions/2026-04-13-main-abc.md`（session 摘要）
- `→ topics/project-architecture`（主题详情）
- `→ archive/daily/2026-04-08.md`（已归档的源文件）

⚠️ Nightly 整合时，daily 源文件移入 `memory/archive/daily/` 而非删除，保持引用链完整。

---

## 记忆元数据

每条记忆条目应包含元数据，便于检索和过期管理：

```markdown
- REST API 分页参数 bug：同时传 page 和 offset 时 offset 被忽略。解决：拆成两个独立查询接口
  tags: [api, bug, pagination]
  updated: 2026-04-17
  source: → daily/2026-04-17
```

### 元数据字段

| 字段 | 必填 | 说明 |
|------|------|------|
| `tags` | 是 | 2-4 个标签，用于跨主题检索 |
| `updated` | 是 | 最后更新日期 YYYY-MM-DD |
| `source` | 推荐 | 来源链接，便于溯源 |
| `valid_until` | 可选 | 过期日期，用于时效性判断（如 "2026-05-06" 表示入职后可能失效） |

### 标签规范

- 标签用小写英文，用连字符连接（如 `api`、`bug-fix`、`frontend`）
- 同一概念使用统一标签，避免同义词分裂（如统一用 `postgres` 而非混用 `PostgreSQL`/`pg-db`）
- 标签由各实例根据用户实际情况自定义，不预设固定标签集。示例：`[工程]` api, bug-fix, csharp, go, web `[项目]` project-name, release, deploy `[生活]` career, health, travel `[Agent]` skill, automation, heartbeat

### 过期机制

- 带 `valid_until` 的条目，Nightly 在 freshness review 时优先检查是否过期
- 过期条目降级到 dormant 而非直接删除，保留可追溯性
- 没有 `valid_until` 的条目，由 Nightly Agent 基于语义判断是否仍然有效

---

## 时序数据分离

### 原则

重复性、周期性的时序数据**不写入** memory 系统，写入独立的数据通道。

### 识别标准

以下类型的数据属于时序数据，不应使用 `workspace_memory_append`：
- 周期性监控快照（如每小时服务监控：CPU/内存/请求数/错误率）
- 无状态变化的例行记录（如"服务正常、无异常、交还heartbeat"）
- 仪表盘式数据（如账户余额、队列长度、在线人数）

### 处理方式

- 监控数据写入 `workspace/data/snapshots/YYYY-MM-DD.jsonl`（JSON Lines 格式）
- daily memory 中**只记录状态变化事件**：故障、恢复、配置变更、异常事件、结论性判断
- Heartbeat automation 的监控输出直接写 data 目录，不经过 memory

### 示例

❌ 不该写的：`服务状态：正常运行 | 账户余额：1,234.56 | CPU 12% | 无异常，交给heartbeat正常监控`

✅ 该写的：`REST API分页bug：同时传page和offset时offset字段被忽略。解决：拆成两个独立查询接口` 或 `部署流程从手动改为CI/CD，发布时间从30分钟缩短到5分钟`

---

## Topics 主题索引

`workspace/memory/topics/` 是**唯一的** topics 存储位置。Topics 由 Nightly Consolidation 自动维护，日常对话中也可直接创建/更新。

```
workspace/memory/topics/
  project-architecture.md
  code-discipline.md
  frontend-architecture.md
  deployment-guide.md
```

### 目录唯一性

- ⚠️ `workspace/topics/` 是遗留目录，新内容一律写入 `workspace/memory/topics/`
- Nightly 整合时检查 `workspace/topics/` 是否存在文件，如有则迁移到 `workspace/memory/topics/` 并合并重复内容
- topics 总数上限 15 个，超出时合并同类或归档到 dormant

### Topic 文件格式

```markdown
# 前端架构

## 概述
团队前端技术选型和架构演进记录。

## 相关会话
- 2026-03-25：决定采用 React + Vite（来源：sessions/2026-03-25-main-abc123.md）
- 2026-03-20：评估 Next.js vs Vite（来源：sessions/2026-03-20-main-xyz789.md）

## 当前状态
已确定 React + Vite 方案，正在搭建脚手架。

---
last_updated: 2026-03-25
tags: [frontend, react, vite]
```

### 查阅 Topics

```
workspace_read(target="topics")                              # 列出所有 topic 文件
workspace_read(target="topics", path="project-architecture")  # 读取指定 topic
```

---

## 搜索记忆

使用 `fs_grep` 按关键词搜索，`fs_glob` 按文件名/目录发现：

```
# 关键词搜索（内容匹配）
fs_grep(pattern="React 框架", path="workspace/")
fs_grep(pattern="api bug", path="workspace/memory/")

# 标签搜索（通过元数据标签）
fs_grep(pattern="tags:.*frontend", path="workspace/MEMORY.md")

# 目录发现（列出文件）
fs_glob(pattern="workspace/memory/dormant/*.md")    # 列出所有温记忆
fs_glob(pattern="workspace/memory/archive/*.md")    # 列出所有冷记忆
fs_glob(pattern="workspace/memory/sessions/*.md")   # 列出所有会话摘要
fs_glob(pattern="workspace/memory/topics/*.md")     # 列出所有主题索引
```

搜索范围覆盖 MEMORY.md、dormant/、archive/、topics/、sessions/ 所有 markdown 文件。

### 搜索策略

- 优先搜索 MEMORY.md Critical 层（最相关、最常访问）
- 其次搜索 Active 层相关主题分组
- 找不到时扩展到 topics/ 和 dormant/
- 最后搜索 archive/（冷记忆，需要明确的溯源需求才搜索）
- 使用 `history_search` 搜索已压缩的对话历史

---

## 写入记忆

### workspace_memory_append — 日常对话的唯一写入路径

```
workspace_memory_append(
  content="REST API 分页参数 bug：同时传 page 和 offset 时 offset 被忽略。解决：拆成两个独立查询接口",
  priority="standard",
  date="2026-04-17"
)
```

日常对话中记录记忆**只用这个工具**。

### 写入判断

写入前先问自己：
1. 这条信息未来 session 会需要吗？（不需要 → 不写）
2. 能从代码/文档实时获取吗？（能 → 不写）
3. 是时序/监控快照吗？（是 → 写 data 目录，不写 memory）
4. 是重复信息吗？（是 → 不写或更新已有条目）
5. 有明确的标签可以归类吗？（有 → 附上 tags）

### 优先级选择

- `permanent`：用户核心身份、长期不变的偏好（工作环境路径、核心价值观）
- `lasting`：重要决策、项目背景、生活变动（入职、搬家、旅行计划）
- `standard`（默认）：技术教训、bug 记录、一般事件
- `ephemeral`：临时信息、当天有效的内容（通常不需要写 memory）

### workspace_protocol_update — Nightly 整合专用

```
workspace_protocol_update(
  target="memory",
  section="工程/技术",
  content="""
- REST API 分页 bug，需拆分为独立查询接口
  tags: [api, bug, pagination] updated: 2026-04-17 source: → daily/2026-04-17
"""
)
```

⚠️ 此工具用于 **Nightly Consolidation Agent 整合时**重写 MEMORY.md。日常对话中不应使用此工具写记忆。

---

## Session 摘要与清理

### 自动摘要

Session 轮转时，系统自动生成结构化摘要存入 `workspace/memory/sessions/`。摘要包含：
- 讨论主题和关键词
- 做出的重要决策
- 用户原话中的关键观点
- 待跟进事项

自动摘要条件：至少 5 条用户消息的对话型 session（automation 类型跳过）。

摘要中不包含密码、API Key、token 等敏感信息。用户说"不用记"/"别记这个"的内容不纳入摘要。

### Session 文件清理

Nightly Consolidation Stage 4 清理规则：
- session 文件 < 500B（空壳或无效摘要）→ 直接归档到 `memory/archive/sessions/`
- 已 consolidated 的 session 摘要，7 天后移入 `memory/archive/sessions/`
- active sessions 保持可搜索，不删除

---

## Auto Dream 整合

每晚 Nightly Consolidation（HEARTBEAT 自动触发）执行六阶段整合：

1. **采集**：读取今日日志 + 未处理的 session 摘要 + 当前 MEMORY.md
2. **整合**：合并重复、解决矛盾、附来源链接和标签，按三层结构写入 MEMORY.md（Critical ≤ 20 条，Active ≤ 50 条）
3. **Topics 维护**：为反复出现的主题创建/更新 topic 文件（≤ 15 个），检查并迁移 `workspace/topics/` 中的孤立文件
4. **清理**：daily 源文件移入 `memory/archive/daily/`（不删除）；清理空壳 session 文件；7 天以上已整合 session 归档
5. **记忆时效审查**：优先检查 `valid_until` 字段判断过期，其次基于语义判断降级。过期条目移到 dormant/ 并更新 frontmatter
6. **索引验证**：检查 MEMORY.md 中所有 `→` 引用链是否完整，标记断链。检查 dormant 文件 > 90 天的自动归档到 archive/

整合完成后，系统自动 git commit 所有 workspace 变更。

---

## 何时不写记忆

**不应该写记忆**：
- 当前对话的临时中间状态
- 可以从代码或文档实时获取的信息
- 不影响未来行为的闲聊内容
- 时序监控快照（无状态变化的例行记录）
- 重复信息（已存在于 MEMORY.md 中）

**应该写记忆**：
- 用户做出重要决策（技术选型、优先级调整）
- 用户分享关键信息（职位变化、项目背景、个人偏好）
- 需要在未来 session 中引用的事件
- 待跟进的承诺或计划
- 教训和 bug 记录（防止重复犯错）

---

## MEMORY.md 与 USER.md 的区别

| 文件 | 用途 | 内容类型 | 变化频率 |
|------|------|---------|---------|
| `USER.md` | 用户画像和稳定偏好 | 角色、工作方式、长期偏好 | 低（月级） |
| `MEMORY.md` | 事件日志和知识索引 | 发生的事、做过的决定、待跟进项 | 高（周级） |
| `topics/` | 主题深度文档 | 某个主题的完整时间线和上下文 | 中（按需更新） |

---

## 读取记忆

```
workspace_read(target="memory")           # 读取完整 MEMORY.md
workspace_read(target="daily_memory")     # 读取今日记忆日志
workspace_read(target="topics")           # 列出所有 topic 文件
workspace_read(target="topics", path="topic-name")  # 读取指定 topic
fs_grep(pattern="关键词", path="workspace/memory/")  # 按内容搜索记忆
fs_glob(pattern="workspace/memory/dormant/*.md")     # 按目录列出温记忆
history_search(query="搜索词", limit=5)              # 搜索已压缩的对话历史
```

### 按需加载策略

- 每次启动只加载 MEMORY.md（已通过分层结构控制信息密度）
- 需要主题详情时 `workspace_read(target="topics", path="xxx")`
- 需要历史细节时 `fs_grep` 搜索 dormant/ 或 archive/
- 需要对话上下文时 `history_search` 搜索压缩历史
- 不要一次性读取多个大文件，按需加载防止上下文膨胀
