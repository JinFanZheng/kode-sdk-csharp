# Tool Events Streaming Guide

## 概述

此 API 现在支持在 Server-Sent Events (SSE) 流中发送 tool 调用事件，让前端可以实时监控 agent 的工具使用情况。

## 事件类型

### 1. 文本内容事件
标准的 OpenAI 格式文本流：
```json
{
  "id": "chatcmpl-xxx",
  "object": "chat.completion.chunk",
  "created": 1234567890,
  "model": "claude-3-5-sonnet-20241022",
  "choices": [
    {
      "index": 0,
      "delta": {
        "content": "Hello"
      },
      "finish_reason": null
    }
  ]
}
```

### 2. Tool 开始事件 (tool:start)
当 agent 开始调用一个 tool 时发送：
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:start",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Pending",
  "timestamp": 1234567890123
}
```

### 3. Tool 完成事件 (tool:end)
当 tool 执行完成时发送：
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:end",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Completed",
  "duration_ms": 150,
  "timestamp": 1234567890273
}
```

### 4. Tool 错误事件 (tool:error)
当 tool 执行出错时发送：
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:error",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Failed",
  "error": "File not found",
  "duration_ms": 50,
  "timestamp": 1234567890173
}
```

## Tool 状态说明

- **Pending**: Tool 调用已注册，等待执行
- **Executing**: Tool 正在执行中
- **Completed**: Tool 成功执行完成
- **Failed**: Tool 执行失败
- **Denied**: Tool 调用被权限控制拒绝
- **Sealed**: Tool 执行已完全结束并记录

## 前端使用示例

### React/TypeScript 示例

```typescript
interface ToolEvent {
  id: string;
  event: 'tool:start' | 'tool:end' | 'tool:error';
  tool_call_id: string;
  tool_name: string;
  state: string;
  error?: string;
  duration_ms?: number;
  timestamp: number;
}

interface TextChunk {
  id: string;
  object: 'chat.completion.chunk';
  created: number;
  model: string;
  choices: Array<{
    index: number;
    delta: { content?: string };
    finish_reason: string | null;
  }>;
}

const ChatComponent = () => {
  const [messages, setMessages] = useState<string>('');
  const [toolCalls, setToolCalls] = useState<ToolEvent[]>([]);

  const streamChat = async (userMessage: string) => {
    const response = await fetch('http://localhost:5149/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        model: 'claude-3-5-sonnet-20241022',
        messages: [{ role: 'user', content: userMessage }],
        stream: true,
      }),
    });

    const reader = response.body?.getReader();
    const decoder = new TextDecoder();

    while (true) {
      const { done, value } = await reader!.read();
      if (done) break;

      const chunk = decoder.decode(value);
      const lines = chunk.split('\n');

      for (const line of lines) {
        if (line.startsWith('data: ')) {
          const data = line.slice(6);
          if (data === '[DONE]') continue;

          try {
            const json = JSON.parse(data);
            
            // 检查是否是 tool 事件
            if (json.event) {
              const toolEvent = json as ToolEvent;
              console.log(`Tool ${toolEvent.event}:`, toolEvent.tool_name);
              setToolCalls(prev => [...prev, toolEvent]);
            } 
            // 否则是文本内容
            else {
              const textChunk = json as TextChunk;
              const content = textChunk.choices[0]?.delta?.content;
              if (content) {
                setMessages(prev => prev + content);
              }
            }
          } catch (e) {
            console.error('Failed to parse SSE data:', e);
          }
        }
      }
    }
  };

  return (
    <div>
      <div className="messages">{messages}</div>
      <div className="tool-calls">
        <h3>Tool Calls:</h3>
        {toolCalls.map((call, idx) => (
          <div key={idx} className={`tool-call ${call.event}`}>
            <span className="tool-name">{call.tool_name}</span>
            <span className="tool-state">{call.state}</span>
            {call.error && <span className="error">{call.error}</span>}
            {call.duration_ms && <span className="duration">{call.duration_ms}ms</span>}
          </div>
        ))}
      </div>
    </div>
  );
};
```

### JavaScript 原生示例

```javascript
async function streamChatWithTools(message) {
  const response = await fetch('http://localhost:5149/v1/chat/completions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model: 'claude-3-5-sonnet-20241022',
      messages: [{ role: 'user', content: message }],
      stream: true,
    }),
  });

  const reader = response.body.getReader();
  const decoder = new TextDecoder();

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    const chunk = decoder.decode(value);
    const lines = chunk.split('\n');

    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const data = line.slice(6).trim();
        if (data === '[DONE]') continue;

        try {
          const json = JSON.parse(data);
          
          if (json.event) {
            // Tool 事件
            console.log(`[${json.event}] ${json.tool_name} (${json.state})`);
            if (json.error) {
              console.error(`  Error: ${json.error}`);
            }
            if (json.duration_ms) {
              console.log(`  Duration: ${json.duration_ms}ms`);
            }
          } else if (json.choices) {
            // 文本内容
            const content = json.choices[0]?.delta?.content;
            if (content) {
              process.stdout.write(content);
            }
          }
        } catch (e) {
          console.error('Parse error:', e);
        }
      }
    }
  }
}
```

## 使用场景

1. **实时进度展示**: 在 UI 中显示 agent 当前正在调用哪些工具
2. **性能监控**: 追踪每个 tool 的执行时间
3. **错误处理**: 实时捕获和展示 tool 执行错误
4. **调试信息**: 帮助开发者理解 agent 的决策过程

## 注意事项

1. 所有事件都通过 SSE 流发送，使用 `data:` 前缀
2. Tool 事件与文本内容事件交错发送
3. 可以通过 `event` 字段区分 tool 事件和文本内容
4. Tool 事件的顺序保证：start → (executing) → end/error
5. 同一个 tool 调用的 `tool_call_id` 在 start/end/error 事件中保持一致
