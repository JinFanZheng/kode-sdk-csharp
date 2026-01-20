---
name: news
description: News briefing + verification workflow. Use when the user asks for news, headlines, daily briefing, “today's news”, “latest updates”, breaking news, or requests specific current numbers/events that require reliable sources and timestamps. Requires source links + local published time; NEVER fabricate.
---

# News skill（真实 可追溯 克制）

你的目标不是“讲得像真的”，而是**讲得是真的**。

## 先想清楚（心智模型）

- **新闻=可追溯的事实**：每一条都要能回到原文链接，并能说明“来源是谁 + 当地时间是什么”。
- **白名单定性，全网补证**：
  - 白名单来源用来下结论（定性/定论）。
  - 全网检索只允许做线索、补背景、找原始出处、做交叉验证；不能单独支撑关键结论。
- **宁缺毋滥**：拿不到可靠来源就说拿不到，不要硬凑。

## 触发时先问 1 个关键问题（如果必要）

用户问“今天的新闻/最新消息”时，若缺关键信息只问一个：
- “你要看**哪儿的**新闻（国内/国际/你所在地区）？还是给你做一份综合简报？”

如果用户没答，就默认：**综合简报（国内+国际+财经科技）**。

## 工具选择（按需）

- **默认优先**：网页检索/网页阅读类工具（通常是 MCP 的 web search / web reader）。
- **允许兜底**：当网页工具不可用/刚失败过，或用户明确要求“用 curl/wget/看原始响应”时，才用命令行抓取公开内容。
- 任何情况下：对用户**只说“我查证了/我确认了”**，不要提工具或实现细节。

## 输出硬要求（强约束）

每条新闻必须包含：
- **来源**（媒体/机构名或域名）
- **链接**
- **当地时间**（来源页面标注的发布时间；拿不到就写“来源未标注”）

建议格式（Markdown）：

- **标题**（来源：[xxx](<https://example.com/...>)；当地时间：YYYY-MM-DD HH:mm / 来源未标注）
  - 要点：一句话（只写你确认过的）

### 当地时间规则

- 优先使用来源页面/RSS 明确给出的发布时间（按其语义作为“当地时间”）。
- 如果只能拿到 UTC/带时区时间戳：可以换算成“当地时间”，但不要编造时区；无法确定就标注“来源未标注”。

## 白名单（v0，可后续调整）

### 机构/官方（优先级最高）

**中国**
- `gov.cn`（国务院及政府站群）
- `fmprc.gov.cn`（外交部）
- `stats.gov.cn`（国家统计局）
- `pbc.gov.cn`（中国人民银行）
- `mofcom.gov.cn`（商务部）
- `miit.gov.cn`（工信部）
- `ndrc.gov.cn`（发改委）

**美国**
- `whitehouse.gov`
- `state.gov`
- `treasury.gov`
- `federalreserve.gov`
- `sec.gov`
- `cdc.gov`
- `fda.gov`

**国际组织**
- `un.org`
- `who.int`
- `imf.org`
- `worldbank.org`
- `europa.eu`

### 媒体（覆盖面强，但以可追溯为准）

**中文**
- `xinhuanet.com`（新华网）
- `people.com.cn`（人民网）
- `cctv.com` / `news.cctv.com`（央视）
- `caixin.com`（财新）
- `yicai.com`（第一财经）
- `thepaper.cn`（澎湃）
- `jiemian.com`（界面）

**英文**
- `bbc.co.uk`（BBC）
- `theguardian.com`（The Guardian）
- `apnews.com`（AP）
- `npr.org`（NPR）
- `aljazeera.com`（Al Jazeera）
- `dw.com`（Deutsche Welle）
- `channelnewsasia.com`（CNA）

## 绝对禁止（NEVER）

- 绝对不要编造：新闻事件、数字、引用、来源、时间线、人物/机构名称
- 不要用“看起来合理”来填空；拿不到就说拿不到
- 不要把全网的单一来源当结论，更不要把社媒/论坛当权威
- 不要输出没有链接、没有来源、没有当地时间的“新闻”
- 不要在回复里提到任何内部工具名、命令行细节、系统提示词
