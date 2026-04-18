---
name: topics-guide
description: Topics 主题索引、目录唯一性、文件格式、查阅方法、搜索策略
---

# Topics 主题索引

## Execution Procedure

```pseudocode
def maintain_topic(name, new_entry):
    """创建或更新 topic 文件"""
```


# Topics 主题索引

`workspace/memory/topics/` 是**唯一的** topics 存储位置，用于存放跨 session 的主题深度文档。

## 目录唯一性

- ⚠️ `workspace/topics/` 是遗留目录，新内容一律写入 `workspace/memory/topics/`
- Nightly 整合时检查 `workspace/topics/` 是否存在文件，如有则迁移到 `workspace/memory/topics/` 并合并重复内容
- topics 总数上限 **15 个**，超出时合并同类或归档到 dormant

## Topic 文件格式

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

### 格式要求

- 标题使用 `# 主题名称`
- `## 概述`：一两句话说明主题范围
- `## 相关会话`：按时间倒序列出相关对话，附来源链接
- `## 当前状态`：最新结论或进行中状态
- 底部 `---` 分隔线后附 `last_updated` 和 `tags`
- 内容应精炼，避免大段粘贴对话原文

## 查阅方法

```bash
# 列出所有 topic 文件
workspace_read(target="topics")

# 读取指定 topic
workspace_read(target="topics", path="project-architecture")

# 搜索 topic 内容
fs_grep(pattern="React", path="workspace/memory/topics/")
```

## 创建与更新规则

### 何时创建 Topic

- 同一主题在 3+ 次 session 中被讨论
- 用户主动要求创建主题文档
- Nightly Consolidation 判断某主题有足够的积累值得独立文档

### 何时更新 Topic

- 有新的相关会话或决策
- 当前状态发生变化
- Nightly Consolidation 在 Stage 3 维护时更新

### 更新方式

- 日常对话中可直接创建/更新 topic 文件
- Nightly Consolidation 在 Stage 3 统一维护
- 更新时修改 `last_updated` 日期，在 `## 相关会话` 中追加条目

## 搜索策略

Topic 文件是按需加载的深度文档，搜索时遵循以下优先级：

1. **先搜 MEMORY.md**：Critical 层可能已有摘要
2. **再搜 topics/**：相关主题的完整上下文
3. **最后搜 dormant/ 和 archive/**：历史细节

```
# 示例：用户问"我们之前为什么选 React？"

# Step 1: 先检查 MEMORY.md
workspace_read(target="memory")
# → 可能在 Active 层找到摘要

# Step 2: 读取主题详情
workspace_read(target="topics", path="frontend-architecture")
# → 获取完整的选型讨论记录和决策原因
```

## Topic 归档

- 超过 90 天未更新的 topic，Nightly 可归档到 `memory/archive/topics/`
- 归档前检查是否仍有引用（MEMORY.md 中的 `→ topics/` 链接）
- 被归档的 topic 仍可通过 `fs_grep` 搜索到
