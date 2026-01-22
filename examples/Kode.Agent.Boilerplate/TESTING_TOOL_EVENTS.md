# 测试 Tool Events 功能

## 快速测试

### 1. 启动后端服务

```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run
```

### 2. 使用 curl 测试

```bash
curl -N http://localhost:5149/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "claude-3-5-sonnet-20241022",
    "messages": [
      {"role": "user", "content": "请读取当前目录下的 README.md 文件，并告诉我它的内容"}
    ],
    "stream": true
  }'
```

### 3. 预期输出示例

你应该看到类似以下的输出：

```
data: {"id":"chatcmpl-xxx","event":"tool:start","tool_call_id":"toolu_abc123","tool_name":"read_file","state":"Pending","timestamp":1234567890123}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"我"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"正在"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","event":"tool:end","tool_call_id":"toolu_abc123","tool_name":"read_file","state":"Completed","duration_ms":150,"timestamp":1234567890273}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"读取"},"finish_reason":null}]}

...

data: [DONE]
```

### 4. 使用 Node.js 测试脚本

创建 `test-tool-events.js`:

```javascript
const https = require('http');

const data = JSON.stringify({
  model: 'claude-3-5-sonnet-20241022',
  messages: [
    { role: 'user', content: '请列出当前目录的文件，然后读取 README.md 的内容' }
  ],
  stream: true
});

const options = {
  hostname: 'localhost',
  port: 5149,
  path: '/v1/chat/completions',
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Content-Length': data.length
  }
};

const req = https.request(options, (res) => {
  console.log(`状态码: ${res.statusCode}\n`);

  res.setEncoding('utf8');
  res.on('data', (chunk) => {
    const lines = chunk.split('\n');
    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const data = line.slice(6).trim();
        if (data === '[DONE]') {
          console.log('\n[完成]');
          continue;
        }

        try {
          const json = JSON.parse(data);
          
          // Tool 事件
          if (json.event) {
            console.log(`\n[Tool ${json.event}]`, {
              name: json.tool_name,
              state: json.state,
              duration: json.duration_ms ? `${json.duration_ms}ms` : undefined,
              error: json.error
            });
          }
          // 文本内容
          else if (json.choices) {
            const content = json.choices[0]?.delta?.content;
            if (content) {
              process.stdout.write(content);
            }
          }
        } catch (e) {
          // 忽略解析错误
        }
      }
    }
  });

  res.on('end', () => {
    console.log('\n\n流结束');
  });
});

req.on('error', (e) => {
  console.error(`请求错误: ${e.message}`);
});

req.write(data);
req.end();
```

运行:
```bash
node test-tool-events.js
```

## 验证要点

✅ **Tool Start 事件**: 每次调用工具时都应该收到 `tool:start` 事件
✅ **Tool End 事件**: 工具执行完成后应该收到 `tool:end` 事件，包含 duration_ms
✅ **Tool Error 事件**: 如果工具执行失败，应该收到 `tool:error` 事件，包含 error 信息
✅ **文本内容**: 正常的文本响应应该继续正常工作
✅ **事件顺序**: Tool 事件应该在相应的文本内容之前或之间发送
✅ **完整性**: 每个 tool:start 都应该有对应的 tool:end 或 tool:error

## 日志检查

在后端日志中，你应该看到：

```
[Stream] Tool started: read_file (ID: toolu_abc123)
[Stream] Tool completed: read_file (ID: toolu_abc123, Duration: 150ms)
```

或者错误情况：

```
[Stream] Tool error: read_file (ID: toolu_abc123) - File not found
```

## 前端集成

参考 `TOOL_EVENTS_GUIDE.md` 中的 React 和 JavaScript 示例，将 tool 事件集成到你的前端应用中。
