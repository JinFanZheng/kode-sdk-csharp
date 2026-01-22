# Kode.Agent Boilerplate

A minimal, production-ready boilerplate for building OpenAI-compatible AI Agent APIs with Kode.Agent SDK.

## Features

✅ **Simple Session Routing**
- Explicit session ID management via URL path or headers
- Automatic session creation and resumption
- Full conversation history persistence

✅ **OpenAI Compatible API**
- `/v1/chat/completions` endpoint
- Streaming (SSE) and non-streaming responses
- Compatible with OpenAI SDKs and tools

✅ **Core Agent Capabilities**
- Builtin tools (fs_read, fs_write, bash_run, etc.)
- MCP (Model Context Protocol) integration
- Skills system support
- Sandbox isolation

✅ **Observability**
- OpenTelemetry tracing
- Structured logging with Serilog
- Activity tracking

✅ **Production Ready**
- Clean architecture
- Minimal dependencies
- Easy to extend

## Quick Start

### 1. Configure API Keys

**Option A: Edit `appsettings.json`**

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-api03-your-key-here",
    "BaseUrl": "https://api.anthropic.com",
    "ModelId": "",
    "EnableBetaFeatures": false
  }
}
```

Or for third-party Anthropic-compatible APIs (like Zhipu GLM):
```json
{
  "Anthropic": {
    "ApiKey": "your-zhipu-api-key",
    "BaseUrl": "https://open.bigmodel.cn/api/anthropic",
    "ModelId": "GLM-4.7",
    "EnableBetaFeatures": false
  }
}
```

**Option B: Set Environment Variables (Recommended for Production)**

Windows (PowerShell):
```powershell
$env:ANTHROPIC_API_KEY="sk-ant-api03-your-key-here"
$env:ANTHROPIC_BASE_URL="https://api.anthropic.com"
$env:ANTHROPIC_MODEL_ID="claude-sonnet-4-20250514"
```

Linux/macOS:
```bash
export ANTHROPIC_API_KEY="sk-ant-api03-your-key-here"
export ANTHROPIC_BASE_URL="https://api.anthropic.com"
export ANTHROPIC_MODEL_ID="claude-sonnet-4-20250514"
```

**For OpenAI:**

```json
{
  "Kode": {
    "DefaultProvider": "openai",
    "DefaultModel": "gpt-4o"
  },
  "OpenAI": {
    "ApiKey": "sk-your-openai-key-here",
    "BaseUrl": "https://api.openai.com/v1",
    "Organization": "",
    "DefaultModel": "gpt-4o"
  }
}
```

Or for Azure OpenAI:
```json
{
  "OpenAI": {
    "ApiKey": "your-azure-key",
    "BaseUrl": "https://your-resource.openai.azure.com",
    "DefaultModel": "gpt-4o"
  }
}
```

Environment variables:
```bash
export OPENAI_API_KEY="sk-your-openai-key-here"
export OPENAI_BASE_URL="https://api.openai.com/v1"
export OPENAI_ORGANIZATION="org-xxx"
export OPENAI_MODEL_ID="gpt-4o"
```

### 2. Run the Application

```bash
cd examples/Kode.Agent.Boilerplate
dotnet run
```

The server will start on `http://localhost:5124`

### 3. Test with curl

**First request (creates new session):**
```bash
curl -X POST http://localhost:5124/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {"role": "user", "content": "Hello! What can you do?"}
    ]
  }'
```

Response includes `X-Session-Id` header (e.g., `agt_ABC123XYZ`)

**Continue conversation:**
```bash
curl -X POST http://localhost:5124/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: agt_ABC123XYZ" \
  -d '{
    "messages": [
      {"role": "user", "content": "Create a file called hello.txt"}
    ]
  }'
```

**Streaming response:**
```bash
curl -X POST http://localhost:5124/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: agt_ABC123XYZ" \
  -d '{
    "messages": [
      {"role": "user", "content": "Write a poem"}
    ],
    "stream": true
  }'
```

## Session Management

### How It Works

1. **First Request (No Session ID)**
   - Server creates new Agent with generated ID (e.g., `agt_ABC123XYZ`)
   - Returns session ID in `X-Session-Id` response header
   - Saves Agent state to `.kode/agt_ABC123XYZ/`

2. **Subsequent Requests (With Session ID)**
   - Client includes `X-Session-Id: agt_ABC123XYZ` in request
   - Server resumes Agent from disk (loads conversation history)
   - Multi-turn conversation continues seamlessly

3. **Persistence**
   - All conversation history saved to `.kode/{sessionId}/runtime/messages.json`
   - Tool execution records saved to `.kode/{sessionId}/runtime/tool-calls.json`
   - Full state restoration even after server restart

### Session ID Formats

Three ways to provide session ID (in priority order):

```bash
# 1. URL Path Parameter
POST /{sessionId}/v1/chat/completions

# 2. X-Session-Id Header (recommended)
POST /v1/chat/completions
X-Session-Id: agt_ABC123XYZ

# 3. X-Kode-Agent-Id Header (legacy compatibility)
POST /v1/chat/completions
X-Kode-Agent-Id: agt_ABC123XYZ
```

## Client Integration Examples

### JavaScript/TypeScript

```typescript
class ChatClient {
  private sessionId: string | null = null;

  async sendMessage(message: string) {
    const headers: HeadersInit = { 'Content-Type': 'application/json' };
    
    if (this.sessionId) {
      headers['X-Session-Id'] = this.sessionId;
    }

    const response = await fetch('http://localhost:5124/v1/chat/completions', {
      method: 'POST',
      headers,
      body: JSON.stringify({
        messages: [{ role: 'user', content: message }]
      })
    });

    // Save session ID from first response
    const returnedSessionId = response.headers.get('X-Session-Id');
    if (returnedSessionId && !this.sessionId) {
      this.sessionId = returnedSessionId;
      localStorage.setItem('sessionId', returnedSessionId);
    }

    return await response.json();
  }
}
```

### Python

```python
import requests

class ChatClient:
    def __init__(self):
        self.session_id = None
        self.base_url = "http://localhost:5124"
    
    def send_message(self, message: str):
        headers = {"Content-Type": "application/json"}
        
        if self.session_id:
            headers["X-Session-Id"] = self.session_id
        
        response = requests.post(
            f"{self.base_url}/v1/chat/completions",
            headers=headers,
            json={"messages": [{"role": "user", "content": message}]}
        )
        
        # Save session ID
        if not self.session_id:
            self.session_id = response.headers.get("X-Session-Id")
        
        return response.json()

# Usage
chat = ChatClient()
print(chat.send_message("Hello!"))
print(chat.send_message("What's 2+2?"))  # Continues same conversation
```

## Configuration

### appsettings.json

```json
{
  "Kode": {
    "WorkDir": "./workspace",          // Agent working directory
    "StoreDir": "./.kode",             // Session storage location
    "DefaultProvider": "anthropic",    // or "openai"
    "DefaultModel": "claude-sonnet-4-20250514",
    "DefaultSystemPrompt": "You are a helpful AI assistant...",
    "Tools": "*",                      // "*" = all tools, or comma-separated list
    "Permissions": {
      "Mode": "auto"                   // "auto", "ask", or "deny"
    }
  },

  "Skills": {
    "Paths": "skills",                 // Where to find skills
    "Trusted": "demo"                  // Auto-activated skills
  },

  "Anthropic": {
    "ApiKey": "",                      // Your API key
    "BaseUrl": "https://api.anthropic.com"
  },

  "OpenAI": {
    "ApiKey": "",
    "BaseUrl": "https://api.openai.com/v1"
All `appsettings.json` values can be overridden with environment variables:

**API Keys:**
```bash
ANTHROPIC_API_KEY=sk-ant-api03-xxx
ANTHROPIC_BASE_URL=https://api.anthropic.com
ANTHROPIC_MODEL_ID=claude-sonnet-4-20250514
ANTHROPIC_ENABLE_BETA=false

OPENAI_API_KEY=sk-xxx
OPENAI_BASE_URL=https://api.openai.com/v1
OPENAI_ORGANIZATION=org-xxx
OPENAI_MODEL_ID=gpt-4o
```

**Configuration:**
```bash
Kode__WorkDir=./workspace
Kode__StoreDir=./.kode
Kode__DefaultProvider=anthropic
Kode__DefaultModel=claude-sonnet-4-20250514
Kode__Tools=*

Skills__Paths=skills
Skills__Trusted=demo

OpenTelemetry__Enabled=true
OpenTelemetry__Exporter=console
```

**Docker Example:**
```bash
docker run -e ANTHROPIC_API_KEY=sk-xxx \
  -e Kode__WorkDir=/data/workspace \
  your-imag
  
  "OpenTelemetry": {
    "ServiceName": "Kode.Agent.Boilerplate",
    "ServiceVersion": "1.0.0",
    "Enabled": false,                  // Set to true to enable
    "Exporter": "console",             // "console" or "otlp"
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

### Environment Variables

```bash
# API Keys
ANTHROPIC_API_KEY=sk-ant-api03-xxx
ANTHROPIC_BASE_URL=https://api.anthropic.com
ANTHROPIC_MODEL_ID=claude-sonnet-4-20250514
ANTHROPIC_ENABLE_BETA=false

OPENAI_API_KEY=sk-xxx
OPENAI_BASE_URL=https://api.openai.com/v1
OPENAI_ORGANIZATION=org-xxx
OPENAI_MODEL_ID=gpt-4o

# Optional Configuration
KODE_WORK_DIR=./workspace
KODE_STORE_DIR=./.kode
```

## Available Tools

### Builtin Tools

The following tools are **automatically registered** and available to the agent:

| Tool | Description | Example |
|------|-------------|---------|
| `fs_read` | Read file contents | Read source code, configs |
| `fs_write` | Write to files | Create/modify files |
| `fs_list` | List directory contents | Browse project structure |
| `fs_glob` | Find files by pattern | Find `*.json` files |
| `fs_grep` | Search text in files | Search for "TODO" |
| `bash_run` | Execute shell commands | Run build scripts, git |
| `bash_stream` | Stream command output | Watch logs in real-time |

**Configuration:**

Tools aextend the agent with custom JavaScript functions. They are **automatically discovered** from the `skills/` directory.

### Demo Skill

The included demo skill shows how to create custom functions:

```
skills/
└── demo/
    ├── skill.yaml    # Skill definition
    └── README.md     # Documentation
```

**Available Functions:**
- `greet(name: string)` - Returns a personalized greeting
- `calculate(a: number, b: number, operation: string)` - Basic math operations

**Usage Example:**
```bash
curl -X POST http://localhost:5124/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {"role": "user", "content": "Use the demo skill to greet Bob"}
    ]
  }'
```

### Creating Your Own Skills

See `skills/demo/README.md` for a complete guide on:
- Skill structure and configuration
- Writing function definitions
- Parameter types and validation
- Best practices and examples

**Quick Start:**

1. Create a new directory in `skills/`:
   ```
   skills/my-skill/
   ```

2. Add `skill.yaml`:
   ```yaml
   name: my-skill
   version: "1.0"
   description: My custom skill
   
   functions:
     - name: my_function
       description: What it does
       parameters:
         - name: input
           type: string
           required: true
       code: |
         return `Processed: ${input}`;
   ```

3. (Optional) Add `README.md` for documentation

4. Restart the application - skills are auto-loaded!
    "Tools": "*"
  }
}
```

### MCP Tools (FeatBit)

FeatBit MCP provides feature flag management tools:
- `featbit_get_flags` - Get feature flags
- `featbit_create_flag` - Create new flags
- `featbit_update_flag` - Update flags
- `featbit_get_segments` - Get user segments
- More tools available via the MCP server

**Adding More MCP Servers:**

Edit `appsettings.json`:
```json
{
  "McpServers": {
    "featbit": {
      "transport": "streamableHttp",
      "url": "https://mcp.featbit.co/mcp"
    },
    "github": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"]
    }
  }
}
```

## Skills System

Skills are located in `skills/` directory. Example structure:

```
skills/
└── demo/
    └── skill.yaml
```

The demo skill provides example functions:
- `greet(name)` - Returns greeting
- `calculate(a, b, operation)` - Basic math

Create your own skills by adding new directories with `skill.yaml` files.

## OpenTelemetry Tracing

When enabled, traces all:
- HTTP requests
- Agent execution
- Tool calls
- Session operations

View traces in:
- Console (default)
- OTLP-compatible backend (Jaeger, Zipkin, etc.)

## Storage Structure

```
.kode/
├── agt_ABC123XYZ/              # Session directory
│   ├── meta.json               # Agent metadata
│   ├── runtime/
│   │   ├── messages.json       # Conversation history
│   │   ├── tool-calls.json     # Tool execution records
│   │   └── todos.json          # Task list
│   └── events/                 # Event logs
└── agt_DEF456UVW/              # Another session
    └── ...
```

## Extending the Boilerplate

### Add Custom Tools

```csharp
// In Program.cs, after registry.RegisterBuiltinTools():
registry.Register(new MyCustomTool());
```

### Add MCP Servers

```json
// In appsettings.json:
"McpServers": {
  "myserver": {
    "transport": "streamableHttp",
    "url": "https://my-mcp-server.com"
  }
}
```

### Add Authentication

```csharp
// In Program.cs:
builder.Services.AddAuthentication(...);
app.UseAuthentication();
```

### Add Hooks (Advanced)

Create custom hooks to intercept Agent execution:

```csharp
var hooks = new Kode.Agent.Sdk.Core.Hooks.Hooks
{
    PreToolUse = async (call, ctx) => {
        // Validate or modify tool calls
        return null;
    }
};

// Add to AgentConfig
config.Hooks = [hooks];
```

## Troubleshooting

### Session Not Found Error

Ensure you're using the same session ID returned in the `X-Session-Id` header.

### MCP Tools Not Loading

Check logs for MCP connection errors. Verify:
1. MCP server URL is accessible
2. Transport type matches server configuration

### OpenTelemetry Not Working

Set `"Enabled": true` in `OpenTelemetry` configuration section.

## License

MIT License - see LICENSE file for details

## Support

- GitHub Issues: [kode-sdk-csharp/issues](https://github.com/JinFanZheng/kode-sdk-csharp/issues)
- Documentation: See `docs/` directory in repository
