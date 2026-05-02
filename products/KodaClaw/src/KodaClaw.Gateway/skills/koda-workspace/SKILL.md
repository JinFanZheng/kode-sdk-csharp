---
name: koda-workspace
description: KodaClaw workspace 协议指南——workspace 文件布局、工具使用模式、各类 session 上下文差异
license: built-in
compatibility: KodaClaw 1.x
allowed-tools: workspace_protocol_update workspace_memory_append workspace_read diagnostics_query schedule_reminder config_update
metadata:
  kind: builtin-core
  version: "1.2"
  tags: "workspace, protocol, memory, identity"
---

# KodaClaw Workspace 协议

## Execution Procedure

```python
def workspace_ops():
    # 写入策略路由
    if user_stable_preference:    workspace_protocol_update(target="user")
    if important_decision:        workspace_protocol_update(target="memory")
    if periodic_task:             workspace_protocol_update(target="heartbeat")
    if one_shot_reminder:         schedule_reminder()          # → references/schedule-reminder.md
    if daily_observation:         workspace_memory_append()
    if self_diagnose:             diagnostics_query()          # → references/diagnostics.md
    if model_config:              config_update()              # → references/config-update.md
    if read_state:                workspace_read()
```

## Workspace 文件布局

| 文件 | 用途 |
|------|------|
| `IDENTITY.md` | Agent 名称、角色与人格定义 |
| `SOUL.md` | 核心行为原则与边界 |
| `USER.md` | 用户画像、偏好和工作方式 |
| `MEMORY.md` | 长期记忆索引（指针，非完整内容） |
| `HEARTBEAT.md` | 定时自动化规则（`## 标题` + `- cron:` 字段） |
| `AGENTS.md` | Session 规则、工具指导、workspace 惯例 |

## 工具映射

### workspace_protocol_update

更新 workspace 文件中的 section。target: `identity` | `soul` | `user` | `memory` | `agents` | `heartbeat`

```
workspace_protocol_update(target="user", section="偏好", content="- 偏好简洁回答\n- 在 CST 时区工作")
```

### workspace_memory_append

向 MEMORY.md 快速追加事实或备注，无需指定 section。默认 priority `standard`，核心价值观用 `permanent`。

### workspace_read

读取 workspace 文件。target: `identity` | `soul` | `user` | `memory` | `agents` | `heartbeat` | `daily_memory` | `channels` | `topics`

### schedule_reminder

一次性定时提醒，用于明确的单次时间点。周期性重复任务用 heartbeat。详见 → `references/schedule-reminder.md`

### diagnostics_query

查询系统诊断事件，用于 Agent 主动自诊断。详见 → `references/diagnostics.md`

### config_update

管理模型提供商账户和模型配置。详见 → `references/config-update.md`

## Session 上下文差异

| 类型 | 加载上下文 | 可交互 |
|------|----------|--------|
| 主对话 | 全部（ID/SOUL/USER/MEMORY/HB/AGENTS） | 是 |
| Channel DM | AGENTS/ID/SOUL/USER/MEMORY + 会话摘要 | 是 |
| Channel 群聊 | AGENTS/ID/SOUL（默认不加 USER） | 有限 |
| 自动化 | AGENTS/ID/SOUL/USER/HB | 否 |

## 记忆写入策略

- 用户稳定偏好/背景 → `workspace_protocol_update(target="user")`
- 重要事件/决策/教训 → `workspace_protocol_update(target="memory")`
- 周期性定时任务 → `workspace_protocol_update(target="heartbeat")`
- 一次性提醒 → `schedule_reminder()`
- 日常观察 → `workspace_memory_append()`
- 临时代话细节、可实时获取的数据 → **不写**

## 技能自安装

1. `fs_write` 写入 `workspace/skills/<name>/SKILL.md`
2. `skill_list` 确认新技能已出现
3. `skill_activate` 在当前 session 激活
