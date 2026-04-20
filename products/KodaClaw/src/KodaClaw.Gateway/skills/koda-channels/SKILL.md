---
name: koda-channels
description: 渠道消息发送指南——channel_send 工具、Telegram/飞书/微信/钉钉格式差异、媒体附件、BindingId 获取
license: built-in
compatibility: KodaClaw 1.x
allowed-tools: channel_send channel_list
metadata:
  kind: builtin-core
  version: "1.3"
  tags: "channels, telegram, feishu, wechat, dingtalk, messaging, media"
---

# KodaClaw Channels — 渠道消息发送指南

## channel_send 工具

```
channel_send(
  bindingId="telegram-personal",
  content="你好，这是一条消息",
  mediaId="media-abc123"   // 可选，发送媒体附件（图片/音频/视频/文件）
)
```

参数说明：
- `bindingId`（必填）：渠道账号标识符，从 ChannelsDesk 获取
- `content`（必填）：消息文本内容
- `mediaId`（可选）：媒体文件 ID。平台支持的媒体类型见下方「各平台格式差异」章节；不支持的类型会自动降级为文本或文件附件

## BindingId 获取方式

1. 打开 ChannelsDesk（Settings → 渠道）
2. 找到目标渠道账号
3. 点击"复制 BindingId"按钮
4. 在工具调用中使用该 ID

BindingId 格式示例：`telegram-personal`、`feishu-work`、`wechat-account`、`dingtalk-team`

## 各平台格式差异

### Telegram

- 支持 Markdown（`**粗体**`、`_斜体_`、`\`代码\``、代码块）
- 消息长度上限：4096 字符（超出自动截断或分段发送）
- 媒体支持：图片（`image/*`）、音频（`audio/*`）、视频（`video/*` → sendVideo，`duration` 自动从 `MediaReference.DurationMs` 或 MediaStore meta 提取）
- 上传失败时降级为纯文本消息并写诊断事件
- 换行用 `\n`

```
channel_send(
  bindingId="telegram-personal",
  content="**今日报告**\n\n- 任务 A 完成\n- 任务 B 进行中"
)
```

### 飞书（Lark）

- 支持 Markdown，但语法略有不同（加粗用 `**text**`）
- 不支持斜体 markdown，建议使用纯文本
- 消息长度上限：约 4000 字符
- 媒体支持：图片（`image/*` → UploadImage + SendImageMessage）、音频（`audio/*` → UploadAudioFile）、视频（`video/*` → UploadVideoFile，支持 duration）
- 媒体消息与文本消息分别发送（一次调用会拆成多条）
- `@` 提及：不支持通过 channel_send 直接 @ 用户

```
channel_send(
  bindingId="feishu-work",
  content="**项目进展**\n完成了本周所有需求，代码审查通过。"
)
```

### 微信（个人号）

- **不支持 Markdown**，发送纯文本，所有格式标记会原样显示
- 消息长度上限：约 2000 字符
- 发送前自动将 Markdown 转换为纯文本
- 媒体支持：图片（`image/*`）、视频（`video/*` → iLink `video_item` type=5）、通用文件（其他类型 → type=4）
- **不支持语音**（iLink 需要 SILK/AMR 转码，当前降级为文件附件发送）

```
channel_send(
  bindingId="wechat-account",
  content="今日工作总结：完成了三个功能模块，修复了两个 bug。明天继续推进剩余需求。"
)
```

### 钉钉（DingTalk）

- 支持 Markdown（通过 `SendMarkdownMessageAsync`，Auto 模式会启发式检测文本是否含 Markdown 标记）
- 媒体支持：图片（`sampleImage`）、音频（`sampleAudio` 带 duration）、视频（`sampleVideo` 带 duration）、任意文件（`sampleFile` 兜底）
- **直聊需先收到用户消息**：钉钉企业内机器人发送直聊必须先缓存 `recipientUserId`，若用户从未给机器人发过消息，`SendAsync` 会抛 `InvalidOperationException`；群聊不受此约束
- 上传失败时降级为纯文本

```
channel_send(
  bindingId="dingtalk-team",
  content="**今日站会**\n- 今日计划：...\n- 昨日完成：..."
)
```

## delivery-mode 对渠道推送的影响

自动化任务（`HEARTBEAT.md`）中，渠道推送受 `delivery-mode` 控制：

| delivery-mode | 行为 |
|--------------|------|
| `none` | 不推送任何渠道 |
| `auto` | 执行完成后自动推送到 `channels` 列表 |
| `approval` | Inbox 审批通过后手动触发推送 |

在对话中（非自动化），直接调用 `channel_send` 不受此设置影响，立即发送。

## 媒体附件发送

发送媒体需要先有 `mediaId`（用户在对话中上传文件后系统返回），然后通过 `mediaId` 参数传入 `channel_send`。

各平台媒体支持矩阵：

| 类型 | Telegram | 飞书 | 微信 | 钉钉 |
|-----|---------|------|------|------|
| 图片 `image/*` | ✅ | ✅ | ✅ | ✅ |
| 音频 `audio/*` | ✅ | ✅ | ❌（降级为文件） | ✅ |
| 视频 `video/*` | ✅ | ✅ | ✅ | ✅ |
| 其他文件 | ❌（走文本） | ❌ | ✅（type=4） | ✅（sampleFile） |

上传失败或不支持时连接器会降级为纯文本/文件附件，不会丢失消息。

## 最佳实践

- **针对平台调整格式**：Telegram/飞书/钉钉 用 Markdown，微信发纯文本
- **钉钉直聊**：对话前需用户先给机器人发过消息；若不确定是否缓存，先用群聊 binding 或等用户先发一条
- **控制消息长度**：超长内容优先使用 Canvas 保存，再推送摘要 + 链接说明
- **批量发送**：多个渠道需分别调用 `channel_send`，每个 bindingId 一次调用
