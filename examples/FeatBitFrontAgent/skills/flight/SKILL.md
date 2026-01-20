---
name: flight
description: Flight status lookup workflow. Use for flight status, delays, cancellations, airport departures/arrivals, or “today/real-time” aviation updates. Prefer airline/airport official sources; always include source link + local update time; NEVER fabricate.
---

# Flight skill（航班：航班号 + 日期 + 机场 + 状态）

## 心智模型

- **航班信息是强实时**：不查证就回答，基本等于瞎说。
- **最稳的来源**：航司官方航班动态页、机场官方到离港大屏/公告；第三方仅作补证。
- **时区/日期很关键**：同一航班号在不同日期状态不同；跨时区更容易误解“今天”。

## 只问 1 个关键问题（仅在必要时）

缺关键信息时只问一个：
- 没航班号： “你有 **航班号** 吗？（比如 MU5355）”
- 有航班号但日期不清： “你查的是 **哪一天（当地日期）** 的这趟航班？”
- 机场/出发到达歧义： “你要看 **出发** 还是 **到达** 状态？”

## 输出硬要求（强约束）

必须包含：
- 航班号 + 日期（当地日期）
- 出发/到达机场（尽量写清楚城市或 IATA 三字码）
- 状态（计划/延误/取消/起飞/到达…）
- **来源 + 链接**
- **当地时间**（来源页面的更新时间/发布时间；拿不到就写“来源未标注”）

建议格式（Markdown）：

- **MUxxxx（YYYY-MM-DD，当地）**：状态（来源：[xxx](<https://example.com/...>)；当地时间：YYYY-MM-DD HH:mm / 来源未标注）
  - 细节：计划/实际起降时间（如来源提供）

## 来源策略（中国为主）

### 白名单（用于下结论）

- 航司官网/官方 App 的 flight status 页面（以该航司为准）
- 机场官网到离港信息/公告（以该机场为准）
- `caac.gov.cn`（民航局：用于政策/公告类，不等同于单一航班实时状态）

### 全网（仅作补证/交叉验证）

- 第三方航班跟踪平台可作线索与交叉验证，但不要作为唯一结论来源（尤其在“延误/取消”上）。

## NEVER（绝对禁止）

- 不要编造航班状态、延误时长、起降时间、登机口、值机柜台
- 不要把“计划时间”说成“实际时间”
- 不要在缺航班号/日期时就随便选一班来回答
