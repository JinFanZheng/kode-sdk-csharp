---
name: memory-index
description: MEMORY.md 分层索引（Critical/Active/Index）、来源溯源、分层规则
---

## Execution Procedure

```pseudocode
def build_index(memories):
    """按 Critical/Active/Index 三层构建 MEMORY.md"""
```


# MEMORY.md 分层索引

`MEMORY.md` 是长期记忆的索引文件，采用三层结构确保关键信息不被淹没。

## 三层结构

```
# Long-Term Memory

## 🔴 Critical（每次必加载，≤ 20 条）
### 用户核心偏好（permanent）
- [偏好内容]

### 当前活跃目标（带 valid_until）
- [目标内容]

## 🟡 Active（按主题分组，每组 ≤ 5 条，总计 ≤ 50 条）
### 工程/技术
- [技术条目]

### 项目/工作
- [项目条目]

### 生活/职业
- [生活条目]

## 📚 Index（仅引用路径，不展开内容）
### Topics 详见 memory/topics/
### Dormant 详见 memory/dormant/
### Archive 详见 memory/archive/
```

## 分层规则

### Critical 层

- **内容范围**：用户核心偏好（permanent）、当前进行中的长期目标、未来 session 极可能需要的信息
- **容量上限**：严格 ≤ 20 条
- **超出处理**：合并同类项或降级到 Active 层
- **写入权限**：日常对话通过 `workspace_memory_append` 追加，Nightly 通过 `workspace_protocol_update` 重写

### Active 层

- **内容范围**：按主题分组的活跃记忆
- **主题分组**：工程/技术、项目/工作、生活/职业、Agent 协作（可扩展）
- **容量上限**：每组 ≤ 5 条，总计 ≤ 50 条
- **超出处理**：合并或降级到 dormant
- **排序**：同组内按 updated 日期倒序，最近的在前

### Index 层

- **内容范围**：只存文件路径和一句话描述，不展开内容
- **用途**：作为进一步检索的入口，需要时通过 `workspace_read` 或 `fs_grep` 按需读取
- **示例**：
  ```
  ### Topics 详见 memory/topics/
  - project-architecture — 前端架构演进记录
  - code-discipline — 代码规范与最佳实践
  ```

## 来源溯源

每条记忆应附来源链接，便于追溯：

| 来源类型 | 格式 | 示例 |
|---------|------|------|
| 当日记忆日志 | `→ daily/YYYY-MM-DD` | `→ daily/2026-04-17` |
| Session 摘要 | `→ sessions/YYYY-MM-DD-main-abc.md` | `→ sessions/2026-04-13-main-abc.md` |
| 主题详情 | `→ topics/topic-name` | `→ topics/project-architecture` |
| 已归档源文件 | `→ archive/daily/YYYY-MM-DD.md` | `→ archive/daily/2026-04-08.md` |

⚠️ Nightly 整合时，daily 源文件移入 `memory/archive/daily/` 而非删除，保持引用链完整。

## workspace_protocol_update 用法

此工具仅 Nightly Consolidation 使用，用于重写 MEMORY.md：

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

参数说明：
- `target`：固定为 `"memory"`
- `section`：目标分组名（对应 Active 层的主题分组）
- `content`：要写入的完整条目内容（包含元数据行）
