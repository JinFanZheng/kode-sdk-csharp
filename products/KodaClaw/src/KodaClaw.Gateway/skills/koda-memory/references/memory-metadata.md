---
name: memory-metadata
description: 记忆元数据规范——标签体系、更新时间、来源溯源、有效期机制
---

# 记忆元数据规范

## Execution Procedure

```python
def format_metadata(content, tags, source):
    """为记忆条目生成标准元数据格式"""
```


每条记忆条目应包含元数据，便于检索和过期管理。

## 元数据格式

```
- 记忆内容描述
  tags: [tag1, tag2, tag3]
  updated: YYYY-MM-DD
  source: → daily/YYYY-MM-DD
  valid_until: YYYY-MM-DD  # 可选
```

### 示例

```
- REST API 分页参数 bug：同时传 page 和 offset 时 offset 被忽略。解决：拆成两个独立查询接口
  tags: [api, bug, pagination]
  updated: 2026-04-17
  source: → daily/2026-04-17
```

```
- 团队决定前端采用 React + Vite，放弃 Next.js 方案
  tags: [frontend, react, vite, decision]
  updated: 2026-03-25
  source: → sessions/2026-03-25-main-abc123.md
```

```
- 新增灰度发布流程，首批 10% 流量
  tags: [devops, deploy, release]
  updated: 2026-04-10
  source: → daily/2026-04-10
  valid_until: 2026-07-01
```

## 字段说明

| 字段 | 必填 | 说明 |
|------|------|------|
| `tags` | 是 | 2-4 个标签，用于跨主题检索 |
| `updated` | 是 | 最后更新日期，格式 YYYY-MM-DD |
| `source` | 推荐 | 来源链接，便于溯源 |
| `valid_until` | 可选 | 过期日期，用于时效性判断 |

## 标签规范

### 命名规则

- 使用小写英文
- 用连字符连接复合词（如 `bug-fix`、`frontend`、`api-design`）
- 同一概念使用统一标签，避免同义词分裂（如统一用 `postgres` 而非混用 `PostgreSQL` / `pg-db`）

### 标签分类参考

标签由各实例根据用户实际情况自定义，不预设固定标签集。以下是常见分类示例：

```
[工程]   api, bug-fix, csharp, go, web, database, caching
[项目]   project-name, release, deploy, migration
[生活]   career, health, travel, schedule
[Agent]  skill, automation, heartbeat
```

### 标签使用原则

- 每条记忆 2-4 个标签，不多不少
- 至少一个领域标签（标识主题归属）+ 至少一个类型标签（标识信息类型）
- 避免过于宽泛的标签（如 `info`、`data`），尽量具体

## 过期机制

### 带 valid_until 的条目

- Nightly Agent 在 freshness review（Stage 5）时优先检查是否过期
- 过期条目降级到 dormant 而非直接删除，保留可追溯性
- 过期但仍有价值的条目，Agent 可选择延长 `valid_until`

### 无 valid_until 的条目

- 由 Nightly Agent 基于语义判断是否仍然有效
- 判断依据：是否被近期对话引用、是否与当前项目相关、是否已被新信息取代

### 过期处理流程

```
Stage 5: 时效审查
  ├─ 扫描所有带 valid_until 的条目
  ├─ valid_until < today → 标记为待降级
  ├─ 语义判断：该条目是否仍有价值？
  │   ├─ 仍有价值 → 延长 valid_until 或保留
  │   └─ 无价值 → 降级到 dormant
  └─ 扫描无 valid_until 的 standard/ephemeral 条目
      ├─ 30+ 天未引用（standard）→ 考虑降级
      └─ 7 天无价值（ephemeral）→ 降级
```
