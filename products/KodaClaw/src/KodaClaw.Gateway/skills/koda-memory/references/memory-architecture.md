---
name: memory-architecture
description: 三层记忆架构（热/温/冷）的生命周期管理、优先级规则、降级机制
---

# 三层记忆架构

## Execution Procedure

```pseudocode
def classify_priority(content):
    """判断记忆优先级：permanent / lasting / standard / ephemeral"""

def should_downgrade(entry, context):
    """判断是否应该降级记忆条目"""
```


# 三层记忆架构

KodaClaw 记忆分为三层，由 Nightly Agent 基于语义判断管理生命周期。

## 层级定义

### 热记忆（Hot）

- **存储位置**：`workspace/MEMORY.md`
- **加载方式**：每次 session 启动时自动载入 system prompt
- **内容**：用户核心偏好（permanent 级）、当前活跃目标、高频引用的技术决策
- **容量**：Critical ≤20 条，Active ≤50 条
- **维护**：Nightly Consolidation 每晚整合，日常通过 `workspace_memory_append` 追加

### 温记忆（Warm）

- **存储位置**：`workspace/memory/dormant/`
- **加载方式**：不自动加载，通过 `fs_grep` / `fs_glob` 按需搜索
- **内容**：一段时间未被访问但仍有价值的记忆
- **触发条件**：从 Active 层降级而来，或新写入但优先级为 ephemeral 的条目
- **恢复**：被搜索命中或被引用时，可由 Nightly 重新提升到 Active 层

### 冷记忆（Cold）

- **存储位置**：`workspace/memory/archive/`
- **加载方式**：仅通过手动检索访问
- **内容**：长期归档的记忆（daily 日志、过期 session 摘要、降级记忆）
- **子目录**：
  - `archive/daily/` — 已整合的每日记忆日志
  - `archive/sessions/` — 已归档的 session 摘要
  - `archive/topics/` — 已归档的主题文件

## 优先级体系

优先级供 Nightly Agent 参考执行降级，非硬编码时间阈值：

| 优先级 | 适用场景 | 降级条件 |
|--------|---------|---------|
| `permanent` | 核心身份/价值观、长期不变的工作环境偏好 | 永不降级 |
| `lasting` | 重要决策、长期项目背景、生活重大变动 | 仅当明显过时或被用户推翻时降级 |
| `standard`（默认） | 一般事件记录、技术教训、bug 记录 | 30+ 天无关联可降级 |
| `ephemeral` | 临时记录、当天有效的内容 | 7 天无价值即可降级 |

## 降级机制

降级由 Nightly Consolidation Agent 在 Stage 5（时效审查）中执行：

1. **优先检查 `valid_until` 字段**：过期条目优先处理
2. **语义判断**：无 `valid_until` 的条目，由 Agent 判断是否仍然有效
3. **降级路径**：Active → dormant → archive（不跳级）
4. **归档操作**：降级时保留完整的元数据（tags、source、updated），便于追溯
5. **恢复机制**：被搜索命中或被新记忆引用时，Nightly 可重新提升层级

### 降级示例

```
# Active 层中的条目，90 天未引用且 valid_until 已过
- 部署流程从手动改为 CI/CD（valid_until: 2026-03-01）
  tags: [devops, deploy]  updated: 2025-12-01  source: → daily/2025-12-01
→ 降级到 dormant/，Nightly 在 Stage 5 执行
```

## 跨层级搜索

搜索时不需要预判记忆在哪一层，按优先级逐层搜索即可：

```
fs_grep(pattern="React", path="workspace/MEMORY.md")         # 先搜热记忆
fs_grep(pattern="React", path="workspace/memory/dormant/")    # 再搜温记忆
fs_grep(pattern="React", path="workspace/memory/archive/")    # 最后搜冷记忆
```
