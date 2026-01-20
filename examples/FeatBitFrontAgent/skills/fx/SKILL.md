---
name: fx
description: Exchange rates / FX lookup workflow. Use for currency exchange rate questions (USD/CNY, EUR/CNY, etc.), “today/latest/real-time” rates, conversion requests, or when you need to cite FX numbers. Default to market mid rate unless user asks for official/bank rates. Must include source link + local update time; NEVER fabricate.
---

# FX skill（汇率：口径先对齐，再给数字）

## 心智模型

- **“汇率”不是一个数字**：可能是市场现价（mid/bid/ask）、官方中间价/参考价、银行现汇/现钞买卖价，口径不同数字就不同。
- **默认口径**：用户没说清楚时，默认给 **市场现价 mid（中间价）**，但要明确写出来。
- **时间敏感**：带“今天/最新/实时/现在”时，必须写来源页面的**当地更新时间**；拿不到就写“来源未标注”。

## 只问 1 个关键问题（仅在必要时）

如果用户没有说清楚口径，只问一次：
- “你要看 **市场现价**（默认 mid）还是 **官方/银行牌价**？”

如果用户说“换算/兑换”，还缺金额时只问一个：
- “你要换算多少金额？按哪种口径（市场现价/银行牌价）？”

## 输出硬要求（强约束）

必须包含：
- **货币对**（如 USD/CNY）
- **口径**（市场 mid / bid/ask；或 官方中间价；或 银行现汇/现钞）
- **来源 + 链接**
- **当地时间**（来源页面的更新时间/发布时间；拿不到就写“来源未标注”）

建议格式（Markdown）：

- **USD/CNY 市场现价（mid）**：X.XXXX（来源：[xxx](<https://example.com/...>)；当地时间：YYYY-MM-DD HH:mm / 来源未标注）
  - 备注：口径说明（可选）

## 来源策略（中国为主，混合：白名单 + 全网补证）

### 白名单（用于下结论）

**官方口径**
- `pbc.gov.cn`（中国人民银行相关公告/数据）
- `cfets.org.cn`（中国外汇交易中心相关发布）

**银行牌价（现汇/现钞）**
- 银行官网牌价页面（例如 `boc.cn` 等；以用户指定银行为准）

### 全网（仅作补证/交叉验证）

- 大型行情/财经数据平台可用于“市场现价”线索，但必须清楚标注来源与更新时间；不要把单一来源当作“官方口径”。

## NEVER（绝对禁止）

- 不要编造任何汇率数字、来源链接、更新时间
- 不要把不同口径混在一起（例如把“银行现钞卖出价”当“市场中间价”）
- 不要暗示“这是官方价”但引用的却是第三方平台
