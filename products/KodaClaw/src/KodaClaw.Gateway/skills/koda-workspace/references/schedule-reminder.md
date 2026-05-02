---
name: schedule-reminder
description: schedule_reminder 工具完整参考——参数格式、与 heartbeat 的区别、prompt 写法要求
---

# schedule_reminder 工具参考

## 参数

| 参数 | 必填 | 说明 |
|------|------|------|
| `prompt` | 是 | 到时触发的 Agent 指令，应自洽、不依赖当前对话上下文 |
| `fireAt` | 是 | ISO 8601 格式，必须包含时区偏移，如 `2026-04-01T15:00:00+08:00` |
| `title` | 否 | 简短标题，显示在 Inbox 条目中 |
| `channels` | 否 | 结果推送的渠道 BindingId 列表，如 `["tg-12345"]` |

## 核心规则

- `fireAt` 必须是未来时间，不接受过去时间
- `prompt` 要写得完整——执行时没有当前对话上下文
- 返回 `{ timerId, fireAt }`，可告知用户已安排

## 与 heartbeat 的区别

| 场景 | 选择 | 原因 |
|------|------|------|
| 明天下午三点提醒我 | `schedule_reminder` | 明确的单次时间点 |
| 每天早上 9 点跑日报 | `heartbeat`（写入 HEARTBEAT.md） | 周期性重复 |
| 4/1 检查服务器 | `schedule_reminder` | 一次性 |
| 每周五下午发周报 | `heartbeat` | 周期性重复 |

## 示例

```python
schedule_reminder(
  prompt="检查 CI 流水线是否全绿，如有失败写入 Inbox",
  fireAt="2026-04-01T10:00:00+08:00",
  title="CI 健康检查"
)
```
