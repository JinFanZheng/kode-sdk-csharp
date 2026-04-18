---
name: nightly-consolidation
description: Auto Dream 六阶段整合流程、session 清理规则、索引验证
---

## Execution Procedure

```pseudocode
def run_nightly_pipeline():
    """执行六阶段 Nightly 整合"""
```


# Nightly Consolidation（Auto Dream）

每晚由 HEARTBEAT 自动触发的六阶段记忆整合流程。

## 触发条件

- HEARTBEAT 在夜间空闲时段自动触发
- 触发后 Nightly Consolidation Agent 接管执行

## 六阶段流程

### Stage 1：采集

读取以下数据源：
- 今日 daily memory 日志（`workspace/MEMORY.md` 中的当日追加内容）
- 未处理的 session 摘要（`workspace/memory/sessions/` 中未标记 consolidated 的）
- 当前 MEMORY.md 完整内容

```
workspace_read(target="memory")              # 当前 MEMORY.md
workspace_read(target="daily_memory")        # 今日日志
fs_glob(pattern="workspace/memory/sessions/*.md")  # 列出 session 摘要
```

### Stage 2：整合

对采集到的内容执行：
1. **去重**：合并重复或高度相似的条目
2. **解决矛盾**：新旧信息冲突时，以更新的为准，附注变更原因
3. **附来源和标签**：确保每条记忆有完整的元数据
4. **按三层结构写入 MEMORY.md**：
   - Critical ≤ 20 条
   - Active ≤ 50 条（每组 ≤ 5 条）
   - Index 层更新路径引用

使用 `workspace_protocol_update` 重写 MEMORY.md：

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

### Stage 3：Topics 维护

1. 扫描近期会话中反复出现的主题
2. 为符合条件的主题创建或更新 topic 文件（≤ 15 个）
3. 检查 `workspace/topics/`（遗留目录）是否存在文件：
   - 有 → 迁移到 `workspace/memory/topics/` 并合并重复内容
   - 无 → 跳过
4. 更新 MEMORY.md Index 层中的 topics 引用

### Stage 4：清理

#### Daily 源文件归档

- 今日 daily 日志整合完成后，源文件移入 `memory/archive/daily/`
- **不删除**，保持引用链完整
- 文件名保持 `YYYY-MM-DD.md` 格式

#### Session 摘要清理

| 条件 | 操作 |
|------|------|
| session 文件 < 500B（空壳或无效摘要） | 直接归档到 `memory/archive/sessions/` |
| 已 consolidated 的 session 摘要 | 7 天后移入 `memory/archive/sessions/` |
| Active sessions | 保持可搜索，不删除 |

归档操作不删除文件，仅移动位置。

### Stage 5：时效审查

1. **优先检查 `valid_until` 字段**：
   - 过期条目 → 降级到 `memory/dormant/`
   - 即将过期（7 天内）→ 标记待审查
2. **语义判断无 `valid_until` 的条目**：
   - `standard` 优先级：30+ 天未被引用 → 考虑降级
   - `ephemeral` 优先级：7 天无价值 → 降级
   - `lasting` 优先级：仅明显过时才降级
   - `permanent` 优先级：永不降级
3. **降级操作**：
   - 从 MEMORY.md 移除
   - 写入 `memory/dormant/` 对应主题文件
   - 保留完整元数据

### Stage 6：索引验证

1. **引用链完整性检查**：
   - 扫描 MEMORY.md 中所有 `→` 引用
   - 验证目标文件是否存在
   - 标记断链（文件缺失的引用）
2. **Dormant 文件归档**：
   - dormant 文件 > 90 天 → 自动归档到 `memory/archive/`
3. **更新 Index 层**：
   - 同步 dormant 和 archive 的引用路径

## 完成与提交

整合完成后，系统自动 git commit 所有 workspace 变更：

```
git add workspace/
git commit -m "nightly consolidation: YYYY-MM-DD"
```

## 异常处理

| 异常 | 处理方式 |
|------|---------|
| MEMORY.md 为空或不存在 | 跳过 Stage 2 整合，从 daily 重建 |
| Session 摘要读取失败 | 跳过该 session，记录警告 |
| Topics 目录不存在 | 自动创建 `workspace/memory/topics/` |
| Dormant 目录不存在 | 自动创建 `workspace/memory/dormant/` |
| Git commit 失败 | 记录错误，不阻塞后续流程 |
