# Kode.Agent WebApi Assistant (OpenAI Compatible)

This example is an ASP.NET WebAPI application that exposes an OpenAI Chat Completions compatible interface with SSE streaming support.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                         WebApiAssistant                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              AssistantService.cs                          │   │
│  │  - OpenAI Chat Completions compatible interface          │   │
│  │  - Authentication & authorization                         │   │
│  │  - Streaming/non-streaming responses                      │   │
│  └────────────────────┬────────────────────────────────────┘   │
│                       │                                         │
│                       ▼                                         │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │           Assistant/AssistantBuilder.cs                  │   │
│  │  - CreateAssistantAsync(): Create assistant               │   │
│  │  - GenerateAgentId(): Generate unique ID                  │   │
│  │  - CreateAgentDependenciesAsync(): Create dependencies    │   │
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

## Core Components

### 1. AssistantBuilder

Main entry point for creating Personal Assistant Agent.

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

### 2. PersonalAssistant Template

Defines default configuration for the assistant:

- **System Prompt**: Capable and reliable execution partner (Koda)
- **Permission Config**: Tool whitelist/blacklist/approval list
- **Skills Config**: Auto-activated and recommended skill lists
- **Runtime Config**: Todo enabled, timeout, concurrency, etc.

### 3. Hooks Policy System

6 core policies intercept model and tool calls:

| Policy                | Description                                      |
| ---------------------- | ----------------------------------------- |
| `LeakProtectionPolicy` | Prevent internal paths and tool names from leaking to users |
| `NetworkPolicy`        | Prioritize MCP tools for network requests |
| `BrowsePolicy`         | Web Reader/Chrome DevTools integration and security checks |
| `VerifyPolicy`         | Location-related and verification request handling |
| `MemoryRecallPolicy`   | Memory-related request handling |
| `EnvInjectorPolicy`    | Inject environment variables like KODE_AGENT_DIR |

## File Structure

```
examples/Kode.Agent.WebApiAssistant/
├── Assistant/                          # Assistant abstraction layer
│   ├── AssistantOptions.cs             # Creation options
│   ├── AssistantTemplate.cs            # PersonalAssistant template
│   ├── AssistantBuilder.cs             # Main entry point
│   └── Hooks/                          # Hooks system
│       ├── Utils/
│       │   ├── ProfileStore.cs         # User configuration management
│       │   └── MessageUtils.cs         # Message extraction utilities
│       ├── Policies/                   # Policy implementations
│       │   ├── LeakProtectionPolicy.cs
│       │   ├── NetworkPolicy.cs
│       │   ├── BrowsePolicy.cs
│       │   ├── VerifyPolicy.cs
│       │   ├── MemoryRecallPolicy.cs
│       │   └── EnvInjectorPolicy.cs
│       └── AssistantHooks.cs           # Policy composer
├── Services/                           # Application services
│   ├── AgentToolsLoader.cs             # Tool loader
│   └── McpServersLoader.cs             # MCP server loader
├── OpenAI/                             # OpenAI compatibility layer
│   ├── OpenAiChatCompletionRequest.cs
│   └── OpenAiChatCompletionResponse.cs
├── Extensions/                         # Extension methods
│   └── PlatformToolsExtensions.cs      # Platform tool registration
├── AssistantService.cs                 # HTTP request handling
├── Program.cs                          # Application entry point
└── appsettings.json                    # Configuration file
```

## Running

```bash
cd examples/Kode.Agent.WebApiAssistant

cp .env.example .env
# Edit .env, at minimum set DEFAULT_PROVIDER + corresponding API KEY

dotnet run
```

Default listening address is shown in console output (typically `http://localhost:5xxx`).

## Endpoints

- `POST /v1/chat/completions` (OpenAI compatible)
  - `stream=false`: Returns JSON
  - `stream=true`: Returns `text/event-stream` (SSE), outputs as `data: ...\n\n`, ends with `data: [DONE]`
- `GET /healthz`

Regarding cancellation/disconnection:

- When client interrupts SSE connection, server stops writing to stream and exits handler, but **will NOT automatically `Interrupt` Agent** (aligned with TS assistant: disconnection ≠ task cancellation).

## Sessions & Memory

This service stores conversation state in JSON files under `KODE_STORE_DIR`.

### Agent ID Resolution Order

1. `user` field in request body (recommended)
2. `X-Kode-Agent-Id` in request headers
3. Auto-generated (returned to client)

### Data Directory Structure

```
<workDir>/
├── .assistant-store/              # Agent persistence storage
│   └── <agentId>/                 # State files for each agent
└── data/                         # User data directory
    ├── .memory/                  # Memory storage
    │   ├── profile.json          # User configuration (timezone, language, etc.)
    │   └── facts/                # Fact memories
    ├── .knowledge/               # Knowledge base
    ├── .config/                  # Configuration files
    │   ├── notify.json           # Notification config (DingTalk/WeCom/Telegram)
    │   └── email.json            # Email config (IMAP/SMTP)
    └── .tasks/                   # Task storage
```

## Tool Whitelist (allowlist)

This service sends allowed tool list as whitelist to Agent: tools not in whitelist are directly denied (won't enter approval pause).

- Default whitelist comes from `KODE_TOOLS` / `Kode:Tools` (the tools you expose to the model)
- Explicit denial: `PermissionConfig.DenyTools`
- Must approve: `PermissionConfig.RequireApprovalTools`

### PersonalAssistant Default Tool Whitelist

```csharp
AllowTools =
[
    // File system (read-only and edit)
    "fs_read", "fs_write", "fs_edit", "fs_grep", "fs_glob", "fs_multi_edit",
    // Email (read and draft)
    "email_list", "email_read", "email_draft", "email_move",
    // Notifications
    "notify_send",
    // Time
    "time_now",
    // MCP network search
    "web_search", "web_reader", "web_search_prime", "read_url",
    // Skills
    "skill_list", "skill_activate", "skill_resource",
    // Todo
    "todo_read", "todo_write",
    // Bash (restricted)
    "bash_run", "bash_logs"
],
```

### Tools Requiring Approval

```csharp
RequireApprovalTools =
[
    "email_send",      // Sending email requires approval
    "email_delete",    // Deleting email requires approval
    "fs_rm",           // Deleting files requires approval
]
```

### Denied Tools

```csharp
DenyTools =
[
    "bash_kill"  // Killing processes is forbidden
]
```

## Skills Configuration

PersonalAssistant template includes Skills configuration:

```csharp
Skills: new SkillsConfig
{
    // Skills search paths
    Paths = new[] { "./.kode/skills", "./skills" },

    // Auto-activated Skills
    Include = new[] { "memory", "knowledge", "email" },

    // Recommended Skills
    Recommend = new[]
    {
        "verify", "news", "weather", "fx", "flight", "rail",
        "itinerary", "hotel", "commute", "food",
        "data-base", "data-analysis", "data-viz", "data-files"
    }
}
```

## Hooks Policy Details

### LeakProtectionPolicy

Prevents internal implementation details from leaking to users:

- Remove internal paths like `.config/`, `.tasks/`
- Remove tool names like `fs_read`, `bash_run`
- Collapse Skill directive content (keep whitelisted skills)
- Normalize source reference format

### NetworkPolicy

Prioritize MCP tools for network requests:

- Detect `curl`/`wget` commands
- If MCP tools available, prefer MCP
- Allow bash after MCP failure

### BrowsePolicy

Security checks for browser-related tools:

- Block access to localhost, private IPs
- Block non-HTTP/HTTPS protocols
- Suggest Chrome DevTools when Web Reader fails

### VerifyPolicy

Handle verification-required requests:

- Detect weather, news, and other verification requests
- Auto-inject verify skill reminder
- Support default location configuration

### MemoryRecallPolicy

Handle memory-related requests:

- Detect "do you remember" memory queries
- Extract keywords and inject memory skill reminder
- Forbid direct memory file operations

### EnvInjectorPolicy

Inject environment variables for bash commands:

- `KODE_AGENT_DIR`: Agent working directory
- `KODE_USER_DIR`: User data directory

## Example Requests

Note: Current version doesn't allow clients to override `model`. If you need to carry `model` field, keep it consistent with server configuration.

Non-streaming:

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model":"claude-sonnet-4-5-20250929",
    "user":"demo",
    "messages":[
      {"role":"system","content":"You are a helpful personal assistant."},
      {"role":"user","content":"Hello, introduce yourself"}
    ],
    "stream":false
  }'
```

Streaming (SSE):

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{
    "model":"claude-sonnet-4-5-20250929",
    "user":"demo",
    "messages":[
      {"role":"user","content":"Summarize today's plan in 3 sentences"}
    ],
    "stream":true
  }'
```

## Configuration

| Environment Variable | Config Key         | Description                   | Default                      |
| ------------------- | ------------------- | ------------------------------ | ---------------------------- |
| `KODE_API_KEY`      | `Kode:ApiKey`       | API authentication key         | None (allow all)             |
| `KODE_WORK_DIR`     | `Kode:WorkDir`      | Working directory              | ContentRootPath              |
| `KODE_STORE_DIR`    | `Kode:StoreDir`     | Storage directory              | `<WorkDir>/.assistant-store` |
| `KODE_MODEL`        | `Kode:DefaultModel` | Default model                  | `claude-sonnet-4-5-20250929` |
| `KODE_TOOLS`        | `Kode:Tools`        | Tool whitelist                 | See PersonalAssistant template|
| `DEFAULT_PROVIDER`  | -                   | Model provider (anthropic/openai) | anthropic                    |
| `ANTHROPIC_API_KEY` | -                   | Anthropic API key              | -                            |
| `OPENAI_API_KEY`    | -                   | OpenAI API key                 | -                            |

## MCP Integration

Configure MCP servers via `appsettings.json`:

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

MCP tool naming format: `mcp__{serverName}__{toolName}`
