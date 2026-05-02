---
name: diagnostics
description: diagnostics_query 工具完整参考——参数策略、主动诊断触发条件、correlationId 全链路追踪
---

# diagnostics_query 工具参考

## 参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| 无参数 | — | 查询最近 60 分钟最多 20 条事件 |
| `level` | 全部 | 过滤：`info` | `warning` | `error` |
| `source` | 全部 | 按来源过滤（如 `gateway`、`runtime`、`automation`） |
| `correlationId` | — | 精确定位某次运行/请求的完整链路 |
| `sinceMinutes` | 60 | 缩小时间窗口（最大 1440） |
| `limit` | 20 | 最大返回条数（最大 50） |

## 返回字段

`ts`、`level`、`source`、`eventType`、`message`、`correlationId`、`sessionId`
不含 attributes 值，避免敏感数据进入上下文。

## 何时主动调用

- 用户反映"刚才好像出错了"或"自动化没有按时执行"
- 自动化任务完成后发现结果异常
- 用户询问"最近有什么错误/警告"

## correlationId 全链路追踪

```python
# Step 1：从宽泛查询中找到可疑事件的 correlationId
diagnostics_query(level="error", sinceMinutes=30)

# Step 2：用它过滤出完整链路
diagnostics_query(correlationId="abc123...", sinceMinutes=60, limit=50)
```

## 参数使用策略

- 已知出错大致时段 → 用 `sinceMinutes` 缩小窗口
- 只关注严重问题 → `level="error"`
- 追查特定自动化或请求 → `correlationId` 精确定位
