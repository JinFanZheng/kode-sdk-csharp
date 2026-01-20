# Kode.Agent WebApi Assistant (OpenAI Compatible)

这个示例是一个 ASP.NET WebAPI 应用，对外暴露 OpenAI Chat Completions 兼容接口，并支持 SSE 流式输出。

## 架构概览

```
┌─────────────────────────────────────────────────────────────────┐
│                         WebApiAssistant                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              AssistantService.cs                          │   │
│  │  - OpenAI Chat Completions 兼容接口                      │   │
│  │  - 认证授权                                               │   │
│  │  - 流式/非流式响应                                        │   │
│  └────────────────────┬────────────────────────────────────┘   │
│                       │                                         │
│                       ▼                                         │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │           Assistant/AssistantBuilder.cs                  │   │
│  │  - CreateAssistantAsync(): 创建助手                       │   │
│  │  - GenerateAgentId(): 生成唯一 ID                        │   │
│  │  - CreateAgentDependenciesAsync(): 创建依赖               │   │
│  └────────────────────┬────────────────────────────────────┘   │
│                       │                                         │
│         ┌─────────────┼─────────────┐                          │
│         ▼             ▼             ▼                          │
│  ┌───────────┐ ┌──────────────┐ ┌─────────────┐              │
│  │Template   │ │   Hooks      │ │  Options    │              │
│  │           │ │              │ │             │              │
│  │Personal   │ │LeakProtect   │ │CreateAssist │              │
│  │Assistant  │ │NetworkPolicy │ │AgentId     │              │
│  │           │ │BrowsePolicy  │ │UserId      │              │
│  │Permission │ │VerifyPolicy  │ │WorkDir     │              │
│  │Prompt     │ │MemoryRecall  │ │Model       │              │
│  │           │ │EnvInjector   │ │Skills      │              │
│  └───────────┘ └──────────────┘ └─────────────┘              │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

## 核心组件

### 1. AssistantBuilder

主入口点，负责创建 Personal Assistant Agent。

```csharp
var agent = await AssistantBuilder.CreateAssistantAsync(
    new CreateAssistantOptions
    {
        AgentId = "my-agent",
        UserId = "user123",
        WorkDir = "./workspace",
        Model = "claude-sonnet-4-5-20250929",
        Skills = skillsConfig,
        Permissions = permissionConfig
    },
    globalDeps,
    serviceProvider,
    loggerFactory,
    cancellationToken);
```

### 2. PersonalAssistant 模板

定义了助手的默认配置：

- **系统提示**：清爽靠谱的执行型搭子（Koda）
- **权限配置**：工具白名单/黑名单/审批列表
- **Skills 配置**：自动激活和推荐的技能列表
- **运行时配置**：Todo 启用、超时时间、并发数等

### 3. Hooks 策略系统

6 个核心策略拦截模型和工具调用：

| 策略                   | 作用                                      |
| ---------------------- | ----------------------------------------- |
| `LeakProtectionPolicy` | 防止内部路径、工具名泄露给用户            |
| `NetworkPolicy`        | 优先使用 MCP 工具进行网络请求             |
| `BrowsePolicy`         | Web Reader/Chrome DevTools 集成和安全检查 |
| `VerifyPolicy`         | 位置相关和验证请求处理                    |
| `MemoryRecallPolicy`   | 记忆相关请求处理                          |
| `EnvInjectorPolicy`    | 注入 KODE_AGENT_DIR 等环境变量            |

## 文件结构

```
examples/Kode.Agent.WebApiAssistant/
├── Assistant/                          # 助手抽象层
│   ├── AssistantOptions.cs             # 创建选项
│   ├── AssistantTemplate.cs            # PersonalAssistant 模板
│   ├── AssistantBuilder.cs             # 主入口点
│   └── Hooks/                          # Hooks 系统
│       ├── Utils/
│       │   ├── ProfileStore.cs         # 用户配置管理
│       │   └── MessageUtils.cs         # 消息提取工具
│       ├── Policies/                   # 策略实现
│       │   ├── LeakProtectionPolicy.cs
│       │   ├── NetworkPolicy.cs
│       │   ├── BrowsePolicy.cs
│       │   ├── VerifyPolicy.cs
│       │   ├── MemoryRecallPolicy.cs
│       │   └── EnvInjectorPolicy.cs
│       └── AssistantHooks.cs           # 策略组合器
├── Services/                           # 应用服务
│   ├── AgentToolsLoader.cs             # 工具加载器
│   └── McpServersLoader.cs             # MCP 服务器加载器
├── OpenAI/                             # OpenAI 兼容层
│   ├── OpenAiChatCompletionRequest.cs
│   └── OpenAiChatCompletionResponse.cs
├── Extensions/                         # 扩展方法
│   └── PlatformToolsExtensions.cs      # 平台工具注册
├── AssistantService.cs                 # HTTP 请求处理
├── Program.cs                          # 应用入口
└── appsettings.json                    # 配置文件
```

## 运行

```bash
cd examples/Kode.Agent.WebApiAssistant

cp .env.example .env
# 编辑 .env，至少设置 DEFAULT_PROVIDER + 对应的 API KEY

dotnet run
```

默认监听地址以控制台输出为准（通常是 `http://localhost:5xxx`）。

## 接口

- `POST /v1/chat/completions`（兼容 OpenAI）
  - `stream=false`：返回 JSON
  - `stream=true`：返回 `text/event-stream`（SSE），以 `data: ...\n\n` 形式输出，并以 `data: [DONE]` 结束
- `GET /healthz`

关于取消/断开连接：

- 客户端中断 SSE 连接时，服务端会停止写入流并退出 handler，但 **不会自动 `Interrupt` Agent**（对齐 TS assistant：断连不等于取消任务）。

## 会话与记忆

本服务会把对话状态存到 `KODE_STORE_DIR` 下的 JSON 文件中。

### Agent ID 解析顺序

1. 请求体中的 `user` 字段（推荐）
2. 请求头中的 `X-Kode-Agent-Id`
3. 自动生成（返回给客户端）

### 数据目录结构

```
<workDir>/
├── .assistant-store/              # Agent 持久化存储
│   └── <agentId>/                 # 每个 agent 的状态文件
└── data/                         # 用户数据目录
    ├── .memory/                  # 记忆存储
    │   ├── profile.json          # 用户配置（时区、语言等）
    │   └── facts/                # 事实记忆
    ├── .knowledge/               # 知识库
    ├── .config/                  # 配置文件
    │   ├── notify.json           # 通知配置（钉钉/企微/Telegram）
    │   └── email.json            # 邮件配置（IMAP/SMTP）
    └── .tasks/                   # 任务存储
```

## 工具白名单（allowlist）

本服务会把允许的工具列表作为白名单（allowlist）下发给 Agent：不在白名单中的工具会被直接拒绝（不会进入审批暂停）。

- 默认白名单取 `KODE_TOOLS` / `Kode:Tools`（即你暴露给模型的那批工具）
- 显式拒绝：`PermissionConfig.DenyTools`
- 必须审批：`PermissionConfig.RequireApprovalTools`

### PersonalAssistant 默认工具白名单

```csharp
AllowTools =
[
    // 文件系统（只读和编辑）
    "fs_read", "fs_write", "fs_edit", "fs_grep", "fs_glob", "fs_multi_edit",
    // 邮件（读和草稿）
    "email_list", "email_read", "email_draft", "email_move",
    // 通知
    "notify_send",
    // 时间
    "time_now",
    // MCP 网络搜索
    "web_search", "web_reader", "web_search_prime", "read_url",
    // Skills
    "skill_list", "skill_activate", "skill_resource",
    // Todo
    "todo_read", "todo_write",
    // Bash (受限)
    "bash_run", "bash_logs"
],
```

### 需要审批的工具

```csharp
RequireApprovalTools =
[
    "email_send",      // 发送邮件需审批
    "email_delete",    // 删除邮件需审批
    "fs_rm",           // 删除文件需审批
]
```

### 禁止的工具

```csharp
DenyTools =
[
    "bash_kill"  // 禁止杀进程
]
```

## Skills 配置

PersonalAssistant 模板包含 Skills 配置：

```csharp
Skills: new SkillsConfig
{
    // Skills 搜索路径
    Paths = new[] { "./.kode/skills", "./skills" },

    // 自动激活的 Skills
    Include = new[] { "memory", "knowledge", "email" },

    // 推荐的 Skills
    Recommend = new[]
    {
        "verify", "news", "weather", "fx", "flight", "rail",
        "itinerary", "hotel", "commute", "food",
        "data-base", "data-analysis", "data-viz", "data-files"
    }
}
```

## Hooks 策略说明

### LeakProtectionPolicy

防止内部实现细节泄露给用户：

- 移除 `.config/`、`.tasks/` 等内部路径
- 移除 `fs_read`、`bash_run` 等工具名
- 折叠 Skill 指令内容（保留白名单中的 skill）
- 规范化来源引用格式

### NetworkPolicy

优先使用 MCP 工具进行网络请求：

- 检测 `curl`/`wget` 命令
- 如果有 MCP 工具可用，优先使用 MCP
- MCP 失败后允许使用 bash

### BrowsePolicy

浏览器相关工具的安全检查：

- 阻止访问 localhost、私有 IP
- 阻止非 HTTP/HTTPS 协议
- Web Reader 失败时建议使用 Chrome DevTools

### VerifyPolicy

处理需要验证的请求：

- 检测天气、新闻等需要查证的请求
- 自动注入 verify skill 提醒
- 支持默认位置配置

### MemoryRecallPolicy

处理记忆相关请求：

- 检测"你记得吗"等记忆查询
- 提取关键词并注入 memory skill 提醒
- 禁止直接操作 memory 文件

### EnvInjectorPolicy

为 bash 命令注入环境变量：

- `KODE_AGENT_DIR`：Agent 工作目录
- `KODE_USER_DIR`：用户数据目录

## 示例请求

注意：当前版本不允许客户端覆盖 `model`，如需携带 `model` 字段，请与服务端配置保持一致。

非流式：

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model":"claude-sonnet-4-5-20250929",
    "user":"demo",
    "messages":[
      {"role":"system","content":"You are a helpful personal assistant."},
      {"role":"user","content":"你好，介绍一下你自己"}
    ],
    "stream":false
  }'
```

流式（SSE）：

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{
    "model":"claude-sonnet-4-5-20250929",
    "user":"demo",
    "messages":[
      {"role":"user","content":"用 3 句话总结一下今天的计划"}
    ],
    "stream":true
  }'
```

## 配置项

| 环境变量            | 配置键              | 说明                           | 默认值                       |
| ------------------- | ------------------- | ------------------------------ | ---------------------------- |
| `KODE_API_KEY`      | `Kode:ApiKey`       | API 认证密钥                   | 无（允许所有人）             |
| `KODE_WORK_DIR`     | `Kode:WorkDir`      | 工作目录                       | ContentRootPath              |
| `KODE_STORE_DIR`    | `Kode:StoreDir`     | 存储目录                       | `<WorkDir>/.assistant-store` |
| `KODE_MODEL`        | `Kode:DefaultModel` | 默认模型                       | `claude-sonnet-4-5-20250929` |
| `KODE_TOOLS`        | `Kode:Tools`        | 工具白名单                     | 见 PersonalAssistant 模板    |
| `DEFAULT_PROVIDER`  | -                   | 模型提供商（anthropic/openai） | anthropic                    |
| `ANTHROPIC_API_KEY` | -                   | Anthropic API 密钥             | -                            |
| `OPENAI_API_KEY`    | -                   | OpenAI API 密钥                | -                            |

## MCP 集成

通过 `appsettings.json` 配置 MCP 服务器：

```json
{
  "McpServers": {
    "chrome-devtools": {
      "command": "npx",
      "args": ["-y", "chrome-devtools-mcp@latest", "--headless=true"]
    },
    "glm-web-search": {
      "transport": "streamableHttp",
      "url": "https://open.bigmodel.cn/api/mcp/web_search_prime/mcp",
      "headers": {
        "Authorization": "Bearer your-token"
      }
    }
  }
}
```

MCP 工具命名格式：`mcp__{serverName}__{toolName}`
