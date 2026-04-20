# KodaClaw 微信接入文档

## 概述

本文档描述如何为 KodaClaw 添加微信个人号接入能力，采用 iLink Bot 协议。

## 参考实现

- **项目**: vibe-remote (https://github.com/cyhhao/vibe-remote)
- **关键文件**:
  - `modules/im/wechat.py` - 主适配器，实现 BaseIMClient
  - `modules/im/wechat_api.py` - iLink HTTP API 封装
  - `modules/im/wechat_cdn.py` - CDN 文件上传/下载
  - `modules/im/wechat_auth.py` - 二维码登录流程
  - `modules/im/base.py` - IM 客户端基类
  - `modules/im/factory.py` - IM 客户端工厂

---

## 1. iLink Bot 协议说明

### 1.1 什么是 iLink

iLink 是一个第三方服务，提供微信个人号的 Bot API。它充当桥梁：

```
微信个人号 <---> iLink 服务 <---> 你的 Bot (KodaClaw)
```

### 1.2 Base URL

```
https://ilinkai.weixin.qq.com
```

所有 API 端点都在此基础上。

> **注意**：早期文档曾记载 `https://api.ilink.bot`，该域名不存在于公网 DNS，为错误记录。
> 正确地址来源：vibe-remote `wechat_auth.py` 的 `DEFAULT_BASE_URL`。

### 1.3 认证方式

- 登录前：无需 token
- 登录后：所有请求带 `Authorization: Bearer {bot_token}` header

### 1.4 通用 Headers

```json
{
  "Content-Type": "application/json",
  "AuthorizationType": "ilink_bot_token",
  "X-WECHAT-UIN": "<随机uint32转base64>",
  "Authorization": "Bearer {bot_token}"
}
```

---

## 2. 登录流程

### 2.1 获取二维码

**请求:**
```
GET {base_url}/ilink/bot/get_bot_qrcode?bot_type=3
```

**响应:**
```json
{
  "qrcode": "deb064a6a5fc4df653fc304f9a4a2611",
  "qrcode_img_content": "https://liteapp.weixin.qq.com/q/7GiQu1?qrcode=...&bot_type=3",
  "ret": 0
}
```

- `qrcode`: 二维码 token，用于后续轮询
- `qrcode_img_content`: 二维码图片 **URL**（可直接用 `<img src>` 渲染）
- **Content-Type 为 `application/octet-stream`**（非 JSON），客户端需先 `ReadAsString` 再反序列化

### 2.2 轮询扫码状态

**请求:**
```
GET {base_url}/ilink/bot/get_qrcode_status?qrcode={qrcode}
Header: iLink-App-ClientVersion: 1
```

> **Content-Type 同样为 `application/octet-stream`**，需 ReadAsString 后再反序列化。

**响应状态:**

| status | 含义 |
|--------|------|
| `wait` | 等待扫码 |
| `scaned` | 已扫码，等待确认 |
| `confirmed` | 已确认，登录成功 |
| `expired` | 二维码过期 |

**登录成功响应:**
```json
{
  "status": "confirmed",
  "bot_token": "il_bot_xxxxx",
  "ilink_bot_id": "xxx",
  "ilink_user_id": "xxx"
}
```

### 2.3 检查登录状态

**请求:**
```
POST {base_url}/getLoginStatus
Body: { "token": "{bot_token}" }
```

**响应:**
```json
{
  "ret": 0  // 0=已登录
}
```

---

## 3. 消息收发

### 3.1 接收消息（长轮询）

**请求:**
```
POST {base_url}/ilink/bot/getupdates
Body: {
  "get_updates_buf": "",
  "base_info": { "channel_version": "kodaclaw" }
}
```

- `get_updates_buf`: 游标，首次为空，后续用上次返回的值
- 服务器会 hold 最多 35 秒，有消息才返回

**响应:**
```json
{
  "ret": 0,
  "msgs": [
    {
      "message_id": "xxx",
      "from_user_id": "wxid_xxx",
      "context_token": "ctx_xxx",
      "item_list": [
        {
          "type": 1,
          "text_item": { "text": "你好" }
        }
      ]
    }
  ],
  "get_updates_buf": "new_cursor_xxx",
  "longpolling_timeout_ms": 35000
}
```

### 3.2 消息类型 (item_list type)

| type | 类型 | 数据结构 |
|------|------|----------|
| 1 | 文本 | `text_item: { text: "内容" }` |
| 2 | 图片 | `image_item: { media: {...} }` |
| 3 | 语音 | `voice_item: { media: {...}, text: "转文字", playtime: 5000 }` |
| 4 | 文件 | `file_item: { media: {...}, file_name: "xxx.pdf", len: "1024" }` |
| 5 | 视频 | `video_item: { media: {...} }` |

### 3.3 发送消息

**请求:**
```
POST {base_url}/ilink/bot/sendmessage
Body: {
  "msg": {
    "from_user_id": "",
    "to_user_id": "wxid_xxx",
    "client_id": "kodaclaw-xxx",
    "context_token": "ctx_xxx",
    "message_type": 2,
    "message_state": 2,
    "item_list": [
      { "type": 1, "text_item": { "text": "回复内容" } }
    ]
  },
  "base_info": { "channel_version": "kodaclaw" }
}
```

**关键字段:**
- `message_type`: 1=用户消息, 2=Bot消息
- `message_state`: 0=NEW, 1=GENERATING, 2=FINISH
- `context_token`: 从收到的消息中获取，用于关联对话

### 3.4 发送正在输入状态

**请求:**
```
POST {base_url}/ilink/bot/sendtyping
Body: {
  "ilink_user_id": "wxid_xxx",
  "typing_ticket": "ticket_xxx",
  "status": 1
}
```

- `typing_ticket`: 需要先通过 `getconfig` 获取
- `status`: 1=开始输入, 2=取消

### 3.5 获取 typing_ticket

**请求:**
```
POST {base_url}/ilink/bot/getconfig
Body: {
  "ilink_user_id": "wxid_xxx",
  "context_token": "ctx_xxx"
}
```

**响应:**
```json
{
  "ret": 0,
  "typing_ticket": "ticket_xxx"
}
```

---

## 4. 文件/图片/视频

### 4.1 流程概述

```
1. 调用 getuploadurl 获取 CDN 上传地址
2. 上传文件到 CDN（加密）
3. 拿到 encrypt_query_param 和 aes_key
4. 封装成消息发送
```

### 4.2 获取上传 URL

**请求:**
```
POST {base_url}/ilink/bot/getuploadurl
Body: {
  "filekey": "unique_file_key",
  "media_type": 1,  // 1=图片, 2=视频, 3=文件, 4=语音
  "to_user_id": "wxid_xxx",
  "rawsize": 102400,
  "rawfilemd5": "md5hex",
  "filesize": 102456,
  "aeskey": "base64_aes_key"
}
```

**响应:**
```json
{
  "upload_param": {
    "url": "https://cdn.xxx.com/upload",
    "headers": {...}
  }
}
```

### 4.3 上传到 CDN

1. 生成随机 AES key
2. 用 AES 加密文件内容
3. POST 到 upload_param.url
4. 返回 `encrypt_query_param`（文件标识）

### 4.4 发送文件消息

```json
{
  "type": 4,
  "file_item": {
    "media": {
      "encrypt_query_param": "xxx",
      "aes_key": "base64_key",
      "encrypt_type": 1
    },
    "file_name": "document.pdf",
    "len": "102456"
  }
}
```

### 4.5 下载文件

1. 从消息中提取 `encrypt_query_param` 和 `aes_key`
2. GET `{cdn_base_url}/{encrypt_query_param}`
3. 用 AES 解密内容

---

## 5. 错误处理

### 5.1 常见错误码

| errcode | 含义 | 处理方式 |
|---------|------|----------|
| 0 | 成功 | - |
| -14 | 会话过期 | 需要重新扫码登录 |
| 其他 | 请求失败 | 记录日志，重试 |

### 5.2 会话过期处理

当收到 `errcode: -14` 时：
1. 标记 `is_logged_in = false`
2. 提示用户通过 Web UI 重新扫码
3. 停止消息轮询，等待新 token

---

## 6. KodaClaw 实现指南

### 6.1 目录结构

```
KodaClaw.Gateway/
├── Channels/
│   ├── IChannel.cs           # 现有接口
│   ├── TelegramChannel.cs    # 现有实现
│   ├── WeChat/
│   │   ├── WeChatChannel.cs      # 主适配器
│   │   ├── WeChatApiClient.cs    # HTTP API 封装
│   │   ├── WeChatCdnClient.cs    # CDN 上传下载
│   │   ├── WeChatAuthManager.cs  # 登录流程管理
│   │   ├── WeChatModels.cs       # 数据模型
│   │   └── WeChatConfig.cs       # 配置类
```

### 6.2 核心接口实现

WeChatChannel 需要实现的功能：

```csharp
public class WeChatChannel : IChannel
{
    // 配置
    public string BaseUrl { get; set; } = "https://api.ilink.bot";
    public string BotToken { get; set; }
    
    // 登录
    public Task<QrCodeResult> GetQrCodeAsync();
    public Task<LoginStatus> PollLoginStatusAsync(string qrcode);
    public Task<bool> CheckLoginStatusAsync();
    
    // 消息
    public Task StartPollingAsync(CancellationToken ct);
    public Task SendMessageAsync(string userId, string contextToken, string text);
    public Task SendTypingAsync(string userId, string ticket);
    
    // 文件
    public Task<string> UploadFileAsync(string userId, string filePath);
    public Task<byte[]> DownloadFileAsync(string encryptQueryParam, string aesKey);
    
    // 事件
    public event EventHandler<WeChatMessage> OnMessage;
    public event EventHandler<LoginStatusChanged> OnLoginStatusChanged;
}
```

### 6.3 配置项

```json
{
  "Channels": {
    "WeChat": {
      "Enabled": true,
      "BaseUrl": "https://api.ilink.bot",
      "CdnBaseUrl": "https://api.ilink.bot",
      "BotToken": "",
      "PollTimeoutMs": 35000,
      "AllowedUsers": []
    }
  }
}
```

### 6.4 消息转换

将 iLink 消息转换为 KodaClaw 内部格式：

```csharp
public class WeChatMessage
{
    public string MessageId { get; set; }
    public string FromUserId { get; set; }
    public string ContextToken { get; set; }
    public string Text { get; set; }
    public List<WeChatAttachment> Attachments { get; set; }
    public DateTime Timestamp { get; set; }
}

public class WeChatAttachment
{
    public string Type { get; set; } // image, voice, file, video
    public string Url { get; set; }
    public string FileName { get; set; }
    public long Size { get; set; }
    public string AesKey { get; set; }
    public string EncryptQueryParam { get; set; }
}
```

### 6.5 长轮询实现要点

```csharp
private async Task PollLoop(CancellationToken ct)
{
    string syncBuf = "";
    
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var response = await _api.GetUpdatesAsync(syncBuf, ct);
            
            if (response.ErrCode == -14)
            {
                // 会话过期
                MarkSessionExpired();
                await Task.Delay(30000, ct);
                continue;
            }
            
            syncBuf = response.GetUpdatesBuf;
            SaveSyncBuf(syncBuf); // 持久化游标
            
            foreach (var msg in response.Msgs)
            {
                await ProcessMessageAsync(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll error");
            await Task.Delay(2000, ct);
        }
    }
}
```

### 6.6 游标持久化

将 `get_updates_buf` 保存到文件，重启后恢复，避免重复处理消息：

```csharp
private void SaveSyncBuf(string syncBuf)
{
    var path = Path.Combine(_stateDir, "wechat_sync_buf.json");
    File.WriteAllText(path, JsonSerializer.Serialize(new { sync_buf = syncBuf }));
}

private string LoadSyncBuf()
{
    var path = Path.Combine(_stateDir, "wechat_sync_buf.json");
    if (!File.Exists(path)) return "";
    
    var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
    return json.GetProperty("sync_buf").GetString() ?? "";
}
```

---

## 7. Web UI 集成

### 7.1 登录页面

1. 显示二维码图片
2. 轮询扫码状态
3. 扫码成功后保存 token

### 7.2 状态显示

- 在线/离线状态
- 最后登录时间
- 重新登录按钮

---

## 8. 测试清单

### 8.1 基础功能

- [ ] 获取二维码
- [ ] 扫码登录
- [ ] 检查登录状态
- [ ] 接收文本消息
- [ ] 发送文本消息
- [ ] 正在输入状态

### 8.2 媒体功能

- [x] 接收图片
- [x] 发送图片
- [ ] 接收语音（带转文字）
- [x] 接收文件
- [x] 发送文件
- [ ] 接收视频
- [x] 发送视频（KC-BUG-7204：type=5 video_item / media_type=2，AES key 与 file 同规则）

### 8.3 边界情况

- [ ] 会话过期处理
- [ ] 网络超时重试
- [ ] 消息去重
- [ ] 游标持久化
- [ ] 重启后恢复

---

## 9. 注意事项

1. **Base URL**: 正确地址为 `https://ilinkai.weixin.qq.com`，`api.ilink.bot` 为无效域名
2. **Content-Type**: 所有 iLink 响应均为 `application/octet-stream`，不能用 `ReadFromJsonAsync`，需先 `ReadAsStringAsync` 再反序列化
3. **频率限制**: 长轮询默认 35 秒超时，不要频繁请求
2. **消息去重**: 用 `message_id` 做去重，避免重复处理
3. **会话管理**: `context_token` 关联对话，必须正确传递
4. **文件加密**: CDN 文件需要 AES 加解密
5. **Markdown**: 微信不支持 Markdown，需要转换为纯文本

---

## 10. 参考资料

- vibe-remote 源码: https://github.com/cyhhao/vibe-remote
- 关键文件直接链接:
  - https://raw.githubusercontent.com/cyhhao/vibe-remote/master/modules/im/wechat.py
  - https://raw.githubusercontent.com/cyhhao/vibe-remote/master/modules/im/wechat_api.py
  - https://raw.githubusercontent.com/cyhhao/vibe-remote/master/modules/im/wechat_cdn.py
  - https://raw.githubusercontent.com/cyhhao/vibe-remote/master/modules/im/base.py

---

## 11. 接入教训（Iter 46，2026-03-24 实战总结）

本节记录从 0 到可用文字回复 + 正在输入状态的完整踩坑过程，供下次接入新 IM 渠道时参考。

---

### 11.1 Content-Type 必须是裸 `application/json`，不能带 charset

**现象**：`sendmessage` / `getLoginStatus` 所有 POST 请求返回 HTTP 412。
**根因**：.NET `JsonContent.Create(obj)` 自动生成 `Content-Type: application/json; charset=utf-8`，iLink 服务端对 charset 做严格校验，不匹配即 412。
**正确写法**（适用 **所有** iLink POST 端点）：
```csharp
var json = JsonSerializer.Serialize(body, JsonOptions);
request.Content = new StringContent(json, Encoding.UTF8);
request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
// ❌ 不要用 JsonContent.Create(body)
// ❌ 不要用 new StringContent(json, Encoding.UTF8, "application/json")  ← 会带 charset
```
`MediaTypeHeaderValue("application/json")` 不含 charset，是唯一可靠方式。

---

### 11.2 长轮询必须禁用 HTTP keep-alive

**现象**：`getupdates` 偶发 `HttpIOException: The response ended prematurely (ResponseEnded)`。
**根因**：iLink 服务端在 long-poll 返回后关闭 TCP 连接；.NET HttpClient 默认复用连接，复用到已关闭的连接即 ResponseEnded。
**修复**：在 `getupdates` 请求上加：
```csharp
request.Headers.ConnectionClose = true;
```
注意：其他接口（sendmessage、getconfig 等）不需要，只有长轮询接口才会触发此问题。

---

### 11.3 outbound contextToken 不能存 ThreadBinding，要用内存缓存

**现象**：回复时 `contextToken` 为空，sendmessage 虽然不因此 412（iLink 接受空 contextToken），但会话关联失效。
**根因**：`ChannelOutboundDraft` 只携带 `MetadataJson`（`{ contextToken: "..." }`），而 `MetadataJson` 是从 `ThreadBinding` 里读的，`ThreadBinding` 的 schema 没有 `contextToken` 字段，写入路径不存在。
**方案**：在 `WeChatConnector` 内维护 `ConcurrentDictionary<string, string>`，key = `accountId::fromUserId`，收到消息时写入，发消息时优先读取，fallback 读 `MetadataJson`：
```csharp
// 收消息时
_contextTokenCache[$"{accountId}::{msg.FromUserId}"] = msg.ContextToken;

// 发消息时
var contextToken = _contextTokenCache.TryGetValue($"{accountId}::{draft.ExternalThreadId}", out var cached)
    ? cached
    : ReadContextToken(draft.MetadataJson);
```
**代价**：重启后缓存丢失，但用户发第一条消息后即自动恢复。无需改 SQLite schema，是最小侵入解。

---

### 11.4 ~~每个 Connector 必须在 ChannelDeliveryDispatchService 注册~~（已过时）

> **2026-04-05 更新**：Phase 1 重构后，出站分发改为 `ChannelConnectorKindResolver` 自动索引，不再需要手动在 DispatchService 加 switch case。此条教训已不适用。
>
> **原内容（保留作为历史参考）**：`ChannelDeliveryDispatchService.SendAsync` 曾有 `switch(draft.ConnectorKind)`，新增 connector 必须同步添加 case，否则出站消息会静默丢失（异常被 `catch {}` 吞掉）。这个问题在 2026-04-05 的 Connector Registry 重构中彻底解决。

---

### 11.5 LLM 经常不调用 `channel_send`，需要 fallback 投递

**现象**：简单对话（"哈哈哈"、"好的"）时 Agent 直接输出文本，不调用 `channel_send` 工具，导致 `sentTexts` 为空，无回复。
**根因**：LLM 对纯会话场景倾向于直接回答，不自觉包装成工具调用。
**修复**：在 `ChannelTurnOrchestrator` 末尾，当 `sentTexts.Count == 0` 且 `execution.RawResponse` 非空时，自动用 `SendNotificationAsync` 投递 rawResponse：
```csharp
if (sentTexts.Count == 0 && !string.IsNullOrWhiteSpace(execution.RawResponse))
{
    await _deliveryDispatchService.SendNotificationAsync(account, binding, execution.RawResponse, ...);
}
```
**注意**：fallback catch 不能是 `catch {}`，必须 `catch (Exception ex) { _logger.LogWarning(ex, ...) }` 否则排查困难。

---

### 11.6 iLink 响应 Content-Type 是 `application/octet-stream`，不能用 ReadFromJsonAsync

**现象**（规划阶段发现，实际已在实现中规避）：直接用 `response.Content.ReadFromJsonAsync<T>()` 会因 Content-Type 不匹配而抛异常。
**正确方式**：先 `ReadAsStringAsync`，再 `JsonSerializer.Deserialize<T>`：
```csharp
var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
var result = JsonSerializer.Deserialize<T>(body, JsonOptions);
```

---

### 11.7 sendmessage 412 调试方法

当遇到不明 4xx 错误时，标准诊断步骤：
1. 在 `SendAsync` 加 `_logger.LogInformation("contextTokenLen={Len}", contextToken.Length)` 确认 token 是否为空
2. 检查 `response.IsSuccessStatusCode`，读取错误 body 并记录日志（`EnsureSuccessStatusCode` 只抛异常，不记录 body）
3. 对比 Python 参考实现的请求头，逐字段比对

---

### 11.8 正在输入状态的实现流程

typing indicator 需要两步，不能直接调 sendtyping：
```
getconfig(ilink_user_id, context_token) → typing_ticket
sendtyping(ilink_user_id, typing_ticket, status=1)  // 开始
... Agent 处理中，每 5s 续发一次 sendtyping(status=1) ...
sendtyping(ilink_user_id, typing_ticket, status=2)  // 结束（用 CancellationToken.None，不受 agent ct 影响）
```
getconfig 失败不阻断消息处理，直接降级跳过 typing，保证健壮性。

---

### 11.9 已实现 vs 未实现 API 一览

| API | 状态 | 备注 |
|-----|------|------|
| `get_bot_qrcode` | ✅ | 登录扫码 |
| `get_qrcode_status` | ✅ | 轮询扫描状态 |
| `getLoginStatus` | ✅ | 验证 token 有效性 |
| `getupdates` | ✅ | 长轮询收消息 |
| `sendmessage` | ✅ | 发送文字 |
| `getconfig` | ✅ | 获取 typing_ticket |
| `sendtyping` | ✅ | 正在输入状态 |
| `getuploadurl` | ✅ | 媒体加密上传（image/file/video） |
| `image_item` (type=2) | ✅ | 接收与发送（KC-46xx） |
| `file_item` (type=4) | ✅ | 接收与发送（KC-46xx） |
| `video_item` (type=5) | 🟡 | 出站发送已实现（KC-BUG-7204）；入站接收未实现 |
| `voice_item` (type=3) | ❌ | SILK/AMR 转码超出范围；出站降级为 file |
